"""Builds the documentation site from the repo's Markdown with Zensical.

The pages are the ones in `nav` in zensical.toml. They are copied unchanged into stage/
at their repo paths, so their .md links resolve the same way on GitHub, in the release
zip and on the site. Images they show are copied alongside. Two things GitHub does are
done here: links to repo files that aren't pages go to GitHub, and a line that is only a
user-attachments URL becomes a video.

| Command | Output | URLs |
|---|---|---|
| `build.py` | `site/`, opens from disk | `docs/save-file.html` |
| `build.py serve` | served at localhost:8000 | `docs/save-file.html` |
| `build.py publish` | `public/`, for SITE_URL | `docs/save-file/` |

`publish` builds from `.publish.toml`, a copy of zensical.toml with directory URLs,
`site_url` and instant navigation, in strict mode so a warning fails the build.
Instant navigation needs a server, so local builds go without it.

Every docs/*.md and mods/*/README.md must be in `nav`, and every docs/*.md in the
docs that launcher/package.ps1 puts in the release zip.
"""
import posixpath
import re
import shutil
import subprocess
import sys
import tomllib
from pathlib import Path, PurePosixPath

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
STAGE = HERE / "stage"
CONFIG = HERE / "zensical.toml"
SITE_URL = "https://onehalf.dev/syscall-bbs/"
GITHUB = "https://github.com/KawaiiFiveO/syscall-bbs/blob/main/"


def nav_pages(items):
    for item in items:
        for value in item.values() if isinstance(item, dict) else [item]:
            if isinstance(value, list):
                yield from nav_pages(value)
            else:
                yield value


def check_pages(pages):
    """Fails if a public doc is missing from the nav or from the release zip."""
    errors = []
    for path in sorted(REPO.glob("docs/*.md")) + sorted(REPO.glob("mods/*/README.md")):
        rel = path.relative_to(REPO).as_posix()
        if rel not in pages:
            errors.append(f"{rel} is not in nav in website/zensical.toml")
    package = (REPO / "launcher" / "package.ps1").read_text(encoding="utf-8")
    for path in sorted(REPO.glob("docs/*.md")):
        if f"\\docs\\{path.name}" not in package:
            errors.append(f"docs/{path.name} is not in the docs launcher/package.ps1 copies")
    for rel in pages:
        if not (REPO / rel).is_file():
            errors.append(f"nav names {rel}, which doesn't exist")
    if errors:
        sys.exit("\n".join(errors))


LINK = re.compile(r"\]\(([^)\s#:]+)(#[^)\s]*)?\)")
IMAGE = re.compile(r"!\[[^\]]*\]\(([^)\s#:]+)\)")


def images(rel, text):
    """Repo paths of the images a page shows."""
    base = PurePosixPath(rel).parent
    return [posixpath.normpath(str(base / m.group(1))) for m in IMAGE.finditer(text)]


def offsite_links(rel, text, pages):
    """Points links to repo files that aren't pages at GitHub."""
    base = PurePosixPath(rel).parent

    shown = set(images(rel, text))

    def fix(m):
        target = posixpath.normpath(str(base / m.group(1)))
        if target in pages or target in shown or target.startswith(".."):
            return m.group(0)
        return f"]({GITHUB}{target}{m.group(2) or ''})"

    return LINK.sub(fix, text)


VIDEO = re.compile(r"^(https://github\.com/user-attachments/assets/[\w-]+)[ \t]*$", re.M)


def videos(text):
    return VIDEO.sub(r'<video src="\1" controls muted playsinline></video>', text)


def stage(pages):
    shutil.rmtree(STAGE, ignore_errors=True)
    for rel in pages:
        dst = STAGE / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        text = (REPO / rel).read_bytes().decode("utf-8")
        dst.write_bytes(videos(offsite_links(rel, text, pages)).encode("utf-8"))
        for image in images(rel, text):
            (STAGE / image).parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(REPO / image, STAGE / image)
    shutil.copytree(HERE / "theme", STAGE / "_theme")


def publish_config():
    """Writes .publish.toml: site URL, directory URLs, instant navigation, output in public/."""
    text = CONFIG.read_bytes().decode("utf-8")
    edits = [
        (r"^use_directory_urls = .*$", "use_directory_urls = true"),
        (r"^site_dir = .*$", 'site_dir = "public"'),
        (r"^\[project\]$", f'[project]\nsite_url = "{SITE_URL}"'),
        (r"^features = \[$", 'features = [\n  "navigation.instant",'),
    ]
    for pattern, replacement in edits:
        text, count = re.subn(pattern, replacement, text, flags=re.M)
        if count != 1:
            sys.exit(f"zensical.toml: expected one line matching {pattern}")
    path = HERE / ".publish.toml"
    path.write_bytes(text.encode("utf-8"))
    return path


def main():
    command = sys.argv[1] if len(sys.argv) > 1 else "build"
    if command not in ("build", "serve", "publish"):
        sys.exit(__doc__)
    with CONFIG.open("rb") as f:
        pages = list(nav_pages(tomllib.load(f)["project"]["nav"]))
    check_pages(pages)
    stage(pages)
    zensical = [sys.executable, "-m", "zensical"]
    if command == "publish":
        args = zensical + ["build", "--strict", "-f", str(publish_config())]
    else:
        args = zensical + [command]
    sys.exit(subprocess.call(args, cwd=HERE))


if __name__ == "__main__":
    main()
