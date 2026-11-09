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

# Picks the install to use, asking when there isn't exactly one.
function Select-GameDir {
    $dirs = @(Find-GameDirs)
    if ($dirs.Count -eq 1) { return $dirs[0] }
    if ($dirs.Count -eq 0) { return (Read-Host "Folder containing Last Call BBS.exe") }

    for ($i = 0; $i -lt $dirs.Count; $i++) { Write-Host "  $($i + 1). $($dirs[$i])" }
    $choice = [int](Read-Host "Install to which copy")
    if ($choice -lt 1 -or $choice -gt $dirs.Count) { throw "No such option: $choice" }
    return $dirs[$choice - 1]
}
