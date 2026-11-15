# Textures

The game's images are packed `.tex` files in `Content/Packed/`. `textool` converts them to and from PNG. To replace one, put the new `.tex` at the same path in a mod with `scope = content` (see [Replacing assets](making-mods.md#replacing-assets)).

## textool

`textool.exe` is at `tools/textool/bin/Release/net472/textool.exe` in the release zip or source build.

| Command | Effect |
|---|---|
| `textool unpack <file.tex or folder> <output folder>` | Writes each `.tex` as PNG, with a [settings file](#settings-file) when a setting isn't the default. Recursive; directory structure is kept. |
| `textool pack <file.png, .array folder or folder> <output folder>` | Writes PNGs as `.tex` files. Recursive; directory structure is kept. |
| `textool info <file.tex>...` | Prints each frame's header. |
| `textool check <file.tex or folder>...` | Checks that each file unpacks and packs back unchanged. |

`textool` won't write inside a game folder except under its `mods` folder.

### Replacing a texture

To replace the NETronics Connect desktop icon:

```
textool unpack "<game>/Content/Packed/textures/desktop/icons/terminal.tex" art
```

Edit `art/terminal.png`, then:

```
textool pack art/terminal.png "<game>/mods/MyIcons/Content/Packed/textures/desktop/icons"
```

The replacement may have a different size. Desktop icons are placed in a 100×100 canvas with `crop-offset` and `crop-size`, so a resized icon needs a new `crop-offset`.

### Frames

A file named `<name>.array.tex` holds a list of frames, such as an animation. It unpacks to a folder `<name>.array` with one PNG per frame, numbered from `000.png`. Packing that folder uses every PNG in it, in file name order. Other `.tex` files hold one frame.

## Settings file

A frame's settings are in `<name>.png.cfg` next to its PNG, one `key: value` per line. Lines starting with `#` are comments. Settings left out use the default.

| Key | Effect |
|---|---|
| `format: r` | One-channel frame: the red channel after premultiplying. Default `rgba`. |
| `premultiply-alpha: false` | Stores color without multiplying it by alpha. |
| `blend: additive` | Sets alpha to 0 after premultiplying, for additive drawing. Not with `format: r`. |
| `crop: <x>,<y>,<width>,<height>` | Crop rectangle in pixels. Default the whole image. |
| `crop-offset: <x>,<y>` | Position of the image in its canvas. Default `0,0`. |
| `crop-size: <width>,<height>` | Size of the canvas. Default the image size. |
| `colorblind-mode: <n>` | Colorblind mode number. Default 0. |

The game uses the format its code asks for, not the one in the file, so a replacement must keep the original's `format`. Only some draw calls apply `crop-offset`; desktop icons do.

`unpack` sets `blend: additive` when every alpha is 0 and some color isn't, and `premultiply-alpha: false` when a color value is above its alpha.

## File format

Little-endian. A file is:

| Type | Field |
|---|---|
| `int32` | Version, 1005 |
| `int32` | Frame count |
| | Frames |

Each frame:

| Type | Field |
|---|---|
| `int32` ×2 | Width, height |
| `int32` | Format: 1 = R (1 byte per pixel), 2 = RGBA (4 bytes) |
| `int32` ×2 | Logical width and height, equal to the image size |
| `int32` ×4 | Crop rectangle: x, y, width, height |
| `float32` ×2 | Pivot, 0,0 |
| `float32` ×2 | Crop offset |
| `float32` ×2 | Crop size |
| `int32` | Colorblind mode |
| `int32` | Length of the pixel data |
| bytes | Pixel data: an LZ4 block with no frame header |

Uncompressed pixel data is width × height × bytes per pixel, in rows from the bottom up, with premultiplied alpha (`c * a / 255`, rounded down). `unpack` restores color with `ceil(c * 255 / a)`, which packs back to the same value.
