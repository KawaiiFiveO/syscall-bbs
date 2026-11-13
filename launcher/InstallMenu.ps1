# Shared by install.ps1 and install-release.ps1: dropped paths, the mod menu and copying mods.

# Turns a folder, Last Call BBS.exe or a shortcut to it into the game folder, or $null.
function Resolve-GamePath([string]$path) {
    $path = $path.Trim().Trim('"')
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return $null }
    $item = Get-Item -LiteralPath $path
    if ($item.Extension -eq ".lnk") {
        $target = (New-Object -ComObject WScript.Shell).CreateShortcut($item.FullName).TargetPath
        if (-not $target -or -not (Test-Path -LiteralPath $target)) { return $null }
        $item = Get-Item -LiteralPath $target
    }
    $dir = if ($item.PSIsContainer) { $item.FullName } else { $item.DirectoryName }
    if (Test-Path -LiteralPath (Join-Path $dir "Last Call BBS.exe")) { return $dir.TrimEnd('\') }
    return $null
}

# Sorts paths dropped on the installer into the game folder and mod folders (ones with a mod.ini).
function Split-Dropped([string[]]$paths) {
    $result = [pscustomobject]@{ GameDir = ""; Mods = @() }
    foreach ($path in $paths) {
        if (-not $path) { continue }
        $path = $path.Trim('"')
        if (Test-Path -LiteralPath (Join-Path $path "mod.ini")) {
            $result.Mods += (Get-Item -LiteralPath $path).FullName
            continue
        }
        $game = Resolve-GamePath $path
        if (-not $game) {
            throw "Not the game or a mod: $path`nDrop the game's folder, Last Call BBS.exe, or a mod's folder (one with a mod.ini)."
        }
        $result.GameDir = $game
    }
    $result
}

# Reads a mod folder's mod.ini into an entry for the menu.
function Get-ModEntry([string]$folder, [bool]$default) {
    $ini = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $folder "mod.ini")) {
        if ($line -match '^\s*([^#;=\s][^=]*?)\s*=\s*(.*?)\s*$') { $ini[$Matches[1].ToLowerInvariant()] = $Matches[2] }
    }
    $name = Split-Path $folder -Leaf
    [pscustomobject]@{
        Folder      = $name
        Path        = $folder
        Title       = if ($ini["name"]) { $ini["name"] } else { $name }
        Description = $ini["description"]
        Default     = $default
    }
}

# Picks the mods to install. With $choice ("all", "none" or folder names separated by commas)
# no question is asked; otherwise the user types numbers, or presses Enter for the marked ones.
function Select-Mods([object[]]$entries, [string]$gameDir, [string]$choice) {
    if ($choice) {
        if ($choice -eq "all") { return $entries }
        if ($choice -eq "none") { return @() }
        $names = $choice -split '[,\s]+' | Where-Object { $_ }
        foreach ($n in $names) {
            if (-not ($entries | Where-Object { $_.Folder -eq $n })) {
                throw "No mod named $n. Available: $(($entries | ForEach-Object Folder) -join ', ')"
            }
        }
        return @($entries | Where-Object { $_.Folder -in $names })
    }

    $marked = @()
    Write-Host ""
    Write-Host "Mods:"
    for ($i = 0; $i -lt $entries.Count; $i++) {
        $e = $entries[$i]
        $installed = Test-Path -LiteralPath (Join-Path $gameDir "mods\$($e.Folder)")
        if ($e.Default -or $installed) { $marked += $i + 1 }
        $mark = if ($e.Default -or $installed) { "x" } else { " " }
        $note = if ($installed) { " (installed)" } else { "" }
        Write-Host ("  [{0}] {1}. {2}{3}" -f $mark, ($i + 1), $e.Title, $note)
        if ($e.Description) { Write-Host "         $($e.Description)" }
    }
    Write-Host ""
    Write-Host "Type the numbers of the mods to install, such as 1 3 or 1-4."
    Write-Host "Press Enter for the ones marked [x], or type 0 for none."

    while ($true) {
        $answer = (Read-Host "Mods").Trim()
        if (-not $answer) { $picked = $marked }
        elseif ($answer -eq "0") { $picked = @() }
        else {
            $picked = @()
            foreach ($token in $answer -split '[,\s]+' | Where-Object { $_ }) {
                if ($token -match '^(\d+)-(\d+)$') { $picked += [int]$Matches[1]..[int]$Matches[2] }
                elseif ($token -match '^\d+$') { $picked += [int]$token }
                else { $picked = $null; break }
            }
            if ($null -eq $picked -or ($picked | Where-Object { $_ -lt 1 -or $_ -gt $entries.Count })) {
                Write-Host "Use numbers from 1 to $($entries.Count), separated by spaces or commas."
                continue
            }
        }
        return @($picked | Sort-Object -Unique | ForEach-Object { $entries[$_ - 1] })
    }
}

# Mentions installed mods from the menu that weren't picked; they are left as they are.
function Show-Skipped([object[]]$entries, [object[]]$picked, [string]$gameDir) {
    foreach ($e in $entries) {
        if ($e.Folder -in ($picked | ForEach-Object Folder)) { continue }
        if (Test-Path -LiteralPath (Join-Path $gameDir "mods\$($e.Folder)")) {
            Write-Host "$($e.Title) is installed but wasn't picked, so it wasn't updated. To remove it, delete mods\$($e.Folder)."
        }
    }
}

# Copies a mod folder into the game's mods folder. An existing config.ini is kept.
function Copy-ModFolder([string]$source, [string]$gameDir) {
    $target = Join-Path $gameDir "mods\$(Split-Path $source -Leaf)"
    New-Item -ItemType Directory -Force $target | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $relative = $file.FullName.Substring($source.TrimEnd('\').Length + 1)
        $destination = Join-Path $target $relative
        if ($relative -eq "config.ini" -and (Test-Path -LiteralPath $destination)) { continue }
        New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName $destination -Force
    }
}

# Stops with a clear message if the game is running or the folder can't be written to.
function Assert-CanInstall([string]$gameDir) {
    if (Get-Process -Name "Last Call BBS*" -ErrorAction SilentlyContinue) {
        throw "Last Call BBS is running. Close it and run the installer again."
    }
    # A file that deletes itself when closed.
    $probe = Join-Path $gameDir "syscall-install-$PID.tmp"
    try {
        $stream = New-Object System.IO.FileStream($probe, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None, 4096, [System.IO.FileOptions]::DeleteOnClose)
        $stream.Close()
    } catch [System.UnauthorizedAccessException] {
        throw "Can't write to $gameDir. Right-click Install.cmd and choose Run as administrator."
    }
}

# What the installers put in the game folder, besides mods\.
$LauncherFiles = @("Last Call BBS (Modded).exe", "Last Call BBS (Modded).exe.config", "0Harmony.dll", "Mono.Cecil.dll",
    "LastCallBBS.ModAPI.dll", "LastCallBBS.Launcher.ini", "LastCallBBS.Launcher.log")

function Test-Installed([string]$gameDir) {
    Test-Path -LiteralPath (Join-Path $gameDir "Last Call BBS (Modded).exe")
}

# Asks what to do with an existing installation: "install" (update or change mods) or "uninstall".
function Select-Action {
    Write-Host ""
    Write-Host "SysCall BBS is already installed in this game folder."
    Write-Host "  1. Update it, or change which mods are installed"
    Write-Host "  2. Uninstall it"
    while ($true) {
        $answer = (Read-Host "Choose 1 or 2 (Enter for 1)").Trim()
        if (-not $answer -or $answer -eq "1") { return "install" }
        if ($answer -eq "2") { return "uninstall" }
        Write-Host "Type 1 or 2."
    }
}

# Moves the launcher files and mods\ to the Recycle Bin. Asks first when $confirm is set.
function Uninstall-SysCall([string]$gameDir, [bool]$confirm) {
    $files = @($LauncherFiles | ForEach-Object { Join-Path $gameDir $_ } | Where-Object { Test-Path -LiteralPath $_ })
    $mods = Join-Path $gameDir "mods"
    $hasMods = Test-Path -LiteralPath $mods
    if (-not $files.Count -and -not $hasMods) {
        Write-Host "SysCall BBS isn't installed in $gameDir."
        return
    }

    Write-Host ""
    Write-Host "These will be moved to the Recycle Bin:"
    foreach ($f in $files) { Write-Host "  $(Split-Path $f -Leaf)" }
    if ($hasMods) {
        $names = @(Get-ChildItem -LiteralPath $mods -Directory | ForEach-Object Name)
        if ($names.Count) { Write-Host "  mods\ ($($names -join ', '))" } else { Write-Host "  mods\" }
        $stories = Join-Path $mods "YomielPlayer\stories"
        if (Test-Path -LiteralPath $stories) {
            $count = @(Get-ChildItem -LiteralPath $stories -File -Recurse).Count
            if ($count) { Write-Host "    including $count story file(s) in mods\YomielPlayer\stories" }
        }
    }
    Write-Host "Game saves and mod data in Documents\My Games\Last Call BBS are kept."

    if ($confirm) {
        $answer = Read-Host "Uninstall? (y/N)"
        if ($answer -notmatch '^\s*y') {
            Write-Host "Nothing was changed."
            return
        }
    }

    Add-Type -AssemblyName Microsoft.VisualBasic
    $ui = [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs
    $bin = [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin
    foreach ($f in $files) { [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($f, $ui, $bin) }
    if ($hasMods) { [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($mods, $ui, $bin) }
    Write-Host "Uninstalled. The game's own files are untouched; start it with Last Call BBS.exe."
}
