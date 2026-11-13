# Finds installed copies of the game from the entries their installers leave in the registry.

function Find-GameDirs {
    $ids = @("1468616886_is1", "Steam App 1511780")  # GOG, Steam
    $roots = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
    )
    $found = foreach ($root in $roots) {
        foreach ($id in $ids) {
            $key = Join-Path $root $id
            if (Test-Path -LiteralPath $key) { (Get-ItemProperty -LiteralPath $key).InstallLocation }
        }
    }
    @($found | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ "Last Call BBS.exe")) } |
        ForEach-Object { $_.TrimEnd('\') } | Select-Object -Unique)
}

# Picks the install to use, asking when there isn't exactly one. Uses Resolve-GamePath (InstallMenu.ps1).
function Select-GameDir {
    $dirs = @(Find-GameDirs)
    if ($dirs.Count -eq 1) { return $dirs[0] }
    if ($dirs.Count -eq 0) {
        Write-Host "Last Call BBS wasn't found. Drag the game's folder or Last Call BBS.exe into this window, then press Enter."
        while ($true) {
            $dir = Resolve-GamePath (Read-Host "Game folder")
            if ($dir) { return $dir }
            Write-Host "That folder doesn't contain Last Call BBS.exe."
        }
    }

    Write-Host "Last Call BBS is installed in more than one place:"
    for ($i = 0; $i -lt $dirs.Count; $i++) { Write-Host "  $($i + 1). $($dirs[$i])" }
    while ($true) {
        $answer = (Read-Host "Install to which copy").Trim()
        if ($answer -match '^\d+$' -and [int]$answer -ge 1 -and [int]$answer -le $dirs.Count) { return $dirs[[int]$answer - 1] }
        Write-Host "Type a number from 1 to $($dirs.Count)."
    }
}
