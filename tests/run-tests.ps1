# Checks the mod loader against each installed game build, using throwaway copies of the game's
# exe in a temp folder. Your game folders are only read. Exit code 0 means every check passed.
param(
    # Defaults to every installed copy found in the registry.
    [string[]]$GameDirs = @()
)
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "..\launcher\FindGame.ps1")
if (-not $GameDirs) { $GameDirs = @(Find-GameDirs) }
if (-not $GameDirs) { throw "No installed copy of the game found. Pass -GameDirs." }

# Expect is "loaded", or a pattern that the mod's load failure must match.
$cases = @(
    @{ Name = "ok_foodcourt"; Scope = "foodcourt"; Expect = "loaded" }
    @{ Name = "ok_multi"; Scope = "terminal, story"; Expect = "loaded" }
    @{ Name = "ok_generic"; Scope = "foodcourt"; Expect = "loaded" }
    @{ Name = "ok_config"; Scope = "game"; Expect = "loaded"; ManifestExtra = "name = Config Mod`nversion = 1.2.3"; Config = "greeting = hi there" }
    @{ Name = "full_access"; Scope = "full"; Expect = "loaded" }
    @{ Name = "with_dep"; Scope = "game"; Expect = "loaded"; UsesHelper = $true; ShipHelper = $true; Dll = "with_dep.dll" }
    @{ Name = "report_problem"; Scope = "game"; Expect = "loaded" }
    @{ Name = "throws_on_load"; Scope = "game"; Expect = "boom on load" }
    @{ Name = "partial_patch"; Scope = "full"; Expect = "failed after patching" }
    # Uses a DLL no test mod ships, because a mod can find dependencies in other mods' folders.
    @{ Name = "missing_dep"; Scope = "game"; Expect = "Could not load file or assembly 'helper_unshipped"; UsesUnshipped = $true }
    @{ Name = "outside_hook"; Scope = "foodcourt"; Expect = 'needs "terminal"' }
    @{ Name = "allowed_game"; Scope = "foodcourt"; Expect = "Game needs scope = full"; Allow = $true }
    @{ Name = "uses_harmony"; Scope = "foodcourt"; Expect = "references 0Harmony" }
    @{ Name = "references_game"; Scope = "foodcourt"; Expect = "references the game"; UsesGame = $true }
    @{ Name = "uses_reflection"; Scope = "foodcourt"; Expect = "uses reflection" }
    @{ Name = "dynamic_lookup"; Scope = "foodcourt"; Expect = "uses Activator.CreateInstance" }
    @{ Name = "native_call"; Scope = "foodcourt"; Expect = "calls native code" }
    @{ Name = "bad_helper"; Source = "clean"; Scope = "foodcourt"; Expect = "uses_harmony.dll: references 0Harmony"; Dll = "bad_helper.dll"; ShipBadHelper = $true }
    @{ Name = "no_scope"; Source = "clean"; Scope = $null; Expect = "needs a scope line" }
    @{ Name = "unknown_scope"; Source = "clean"; Scope = "foodcourtt"; Expect = "unknown scope" }
    @{ Name = "two_entries"; Scope = "game"; Expect = "exactly one IMod" }
    @{ Name = "dll_escape"; Source = "clean"; Scope = "game"; Expect = "file name inside"; Dll = "..\x.dll" }
    @{ Name = "bad_id"; Source = "clean"; Scope = "game"; Expect = "needs an id"; Id = "Bad Id!" }
    @{ Name = "no_manifest"; Source = "clean"; Expect = "missing mod.ini"; NoManifest = $true }
    @{ Name = "aa_requires_later"; Source = "clean"; Scope = "game"; Expect = "loaded"; ManifestExtra = "requires = test.zz_required" }
    @{ Name = "ab_after_later"; Source = "clean"; Scope = "game"; Expect = "loaded"; ManifestExtra = "after = test.zz_required, test.not_installed" }
    @{ Name = "zz_required"; Source = "clean"; Scope = "game"; Expect = "loaded" }
    @{ Name = "missing_requirement"; Source = "clean"; Scope = "game"; Expect = 'requires "test.not_installed"'; ManifestExtra = "requires = test.not_installed" }
    @{ Name = "failed_requirement"; Source = "clean"; Scope = "game"; Expect = 'requires "test.throws_on_load"'; ManifestExtra = "requires = test.throws_on_load" }
    @{ Name = "cycle_a"; Source = "clean"; Scope = "game"; Expect = "depends on itself"; ManifestExtra = "after = test.cycle_b" }
    @{ Name = "cycle_b"; Source = "clean"; Scope = "game"; Expect = "depends on itself"; ManifestExtra = "requires = test.cycle_a" }
    @{ Name = "content_only"; Scope = "content"; Expect = "loaded"; NoDll = $true; Content = $true }
    @{ Name = "content_no_scope"; Source = "clean"; Scope = "game"; Expect = "needs scope = content"; Content = $true }
    @{ Name = "zz_check"; Scope = "full"; Expect = "loaded" }
    @{ Name = "zz_dup_id"; Source = "clean"; Scope = "game"; Expect = "already uses"; Id = "test.ok_foodcourt" }
)
$steamCases = @(@{ Name = "steam_blocked"; Scope = "full"; Expect = "loaded" })

$repo = Split-Path $PSScriptRoot
$root = Join-Path $env:TEMP "lastcallbbs-modloader-tests"
$script:failed = 0

function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  pass  $what" }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failed++ }
}

function Build-Case([string]$case, [string]$extraRef = "", [string]$extraName = "") {
    $out = Join-Path $root "build\$case"
    $args = @("build", (Join-Path $PSScriptRoot "TestMod.csproj"), "-c", "Release", "-v", "q",
        "-p:Case=$case", "-p:BaseIntermediateOutputPath=$out\obj\", "-p:OutputPath=$out\bin\")
    if ($extraRef) { $args += "-p:ExtraRef=$extraRef", "-p:ExtraRefName=$extraName" }
    & dotnet @args | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not build test mod $case" }
    return "$out\bin\$case.dll"
}

function Add-Mod($case, [string]$modsDir, [string]$sandbox, [string]$helperDll) {
    $source = if ($case.Source) { $case.Source } else { $case.Name }
    $extraRef = ""
    $extraName = ""
    if ($case.UsesHelper) { $extraRef = $helperDll; $extraName = "helper" }
    if ($case.UsesUnshipped) { $extraRef = Build-Case "helper_unshipped"; $extraName = "helper_unshipped" }
    if ($case.UsesGame) { $extraRef = Join-Path $sandbox "Last Call BBS.exe"; $extraName = "Milwaukee" }
    $dir = Join-Path $modsDir $case.Name
    New-Item -ItemType Directory -Force $dir | Out-Null
    if (-not $case.NoDll) {
        $built = Build-Case $source $extraRef $extraName
        Copy-Item $built (Join-Path $dir "$($case.Name).dll")
    }
    if ($case.Content) {
        foreach ($file in @("emails\test.txt", "sounds\fx\test.ogg", "textures\test.png")) {
            $path = Join-Path $dir "Content\$file"
            New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
            Set-Content $path "overlay $($case.Name)" -Encoding Ascii
        }
    }
    if ($case.ShipHelper) { Copy-Item $helperDll $dir }
    if ($case.ShipBadHelper) { Copy-Item (Build-Case "uses_harmony") $dir }
    if ($case.Config) { Set-Content (Join-Path $dir "config.ini") $case.Config -Encoding Ascii }
    if ($case.NoManifest) { return }

    $id = if ($case.Id) { $case.Id } else { "test.$($case.Name)" }
    $manifest = @("id = $id")
    if ($case.Scope) { $manifest += "scope = $($case.Scope)" }
    if ($case.Dll) { $manifest += "dll = $($case.Dll)" }
    if ($case.ManifestExtra) { $manifest += $case.ManifestExtra -split "`n" }
    Set-Content (Join-Path $dir "mod.ini") $manifest -Encoding Ascii
}

function Test-Build([string]$gameDir) {
    $label = Split-Path (Split-Path $gameDir -Parent) -Leaf
    $isSteam = Test-Path (Join-Path $gameDir "Steamworks.NET.dll")
    $sandbox = Join-Path $root $(if ($isSteam) { "steam" } else { "gog" })
    Write-Host "`n$(if ($isSteam) { 'Steam' } else { 'GOG' }) build ($label)"

    # Test mods left over from an earlier run would break the clean-install checks.
    if (Test-Path $sandbox) { Remove-Item $sandbox -Recurse -Force }
    New-Item -ItemType Directory -Force $sandbox | Out-Null
    foreach ($f in @("Last Call BBS.exe", "Ionic.Zip.Reduced.dll", "Steamworks.NET.dll", "steam_api64.dll")) {
        if (Test-Path (Join-Path $gameDir $f)) { Copy-Item (Join-Path $gameDir $f) $sandbox }
    }

    # A clean install with only our own mods must pass outright.
    & (Join-Path $repo "launcher\install.ps1") -GameDir $sandbox -WithHookLogger -WithFoodCourtTools | Out-Null
    $exe = Join-Path $sandbox "Last Call BBS (Modded).exe"
    $logPath = Join-Path $sandbox "LastCallBBS.Launcher.log"
    $log = Get-Content $logPath
    Check ($log -match "Verify: all mods and hooks are working").Count "clean install verifies"
    Check (($log -match "Hook .*: OK").Count -eq 8) "all 8 hooks attach"
    Check ($log -match "Loaded Skip Loading").Count "SkipLoading loads"
    Check ($log -match "Instant downloads: applied").Count "download patch applies"
    Check ($log -match "No download cooldown: applied").Count "cooldown patch applies"
    Check ($log -match "Loaded Hook Logger").Count "HookLogger passes its scope check"
    Check (($log -match "Loaded Food Court Tools").Count -and -not ($log -match "simulation runner unavailable|level export unavailable").Count) "FoodCourtTools finds its game code"
    if ($isSteam) {
        Check ($log -match "relaunch through Steam disabled").Count "Steam relaunch disabled"
        Check ($log -match "leaderboards blocked \(6 calls\)").Count "leaderboards blocked"
    }
    else {
        Check ($log -match "no Steam integration").Count "Steam guard stays out of GOG"
    }

    # Now add every test mod.
    $helperDll = Build-Case "helper"
    $modsDir = Join-Path $sandbox "mods"
    $all = if ($isSteam) { $cases + $steamCases } else { $cases }
    foreach ($case in $all) { Add-Mod $case $modsDir $sandbox $helperDll }

    # The example isn't installed by default, but must keep working.
    $example = Join-Path $repo "mods\FoodCourtStats"
    & dotnet build (Join-Path $example "FoodCourtStats.csproj") -c Release -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not build the example mod" }
    $exampleDir = Join-Path $modsDir "FoodCourtStats"
    New-Item -ItemType Directory -Force $exampleDir | Out-Null
    Copy-Item (Join-Path $example "bin\Release\FoodCourtStats.dll"), (Join-Path $example "mod.ini") $exampleDir

    $ini = Join-Path $sandbox "LastCallBBS.Launcher.ini"
    foreach ($case in $all | Where-Object { $_.Allow }) { Add-Content $ini "AllowUnscoped.test.$($case.Name) = true" -Encoding Ascii }

    & $exe --verify | Out-Null
    Check ($LASTEXITCODE -eq 2) "--verify reports the broken mods"
    $log = Get-Content $logPath

    foreach ($case in $all) {
        if ($case.Expect -eq "loaded") {
            Check ($log -match "\] Loaded .*\(test\.$($case.Name)\)").Count "$($case.Name) loads"
        }
        else {
            Check ($log -match "^\[ERROR\] \[launcher\] $($case.Name): .*$($case.Expect)").Count "$($case.Name) is rejected"
        }
    }

    Check ($log -match "\[test\.report_problem\] one feature unavailable").Count "reported problem is logged"
    Check ($log -match "still running after the problem").Count "mod keeps running after reporting a problem"
    Check ($log -match "\[test\.with_dep\] helper ok").Count "mod finds its own dependency"
    Check ($log -match "full_access has full access").Count "full-access mod is flagged"
    Check ($log -match "Loaded Food Court Stats").Count "example mod passes its scope check"
    $loadedAt = { param($id) ($log | Select-String -Pattern "\] Loaded .*\(test\.$id\)" | Select-Object -First 1).LineNumber }
    Check ((& $loadedAt "zz_required") -lt (& $loadedAt "aa_requires_later")) "a required mod loads first"
    Check ((& $loadedAt "zz_required") -lt (& $loadedAt "ab_after_later")) "an 'after' mod loads later"
    Check ($log -match "Content overlay: 3 file\(s\) replaced").Count "content overlay counts only the loaded mod's files"
    Check ($log -match "Content overlay: file redirect applied").Count "file redirect applies"
    Check ($log -match "Content overlay: sound redirect applied").Count "sound redirect applies"
    Check ($log -match "Content overlay: image redirect applied").Count "image redirect applies"
    Check ($log -match "Content overlay: redirect verified").Count "replaced file is what the game would read"
    $owners = $log -match "\[test\.zz_check\] patch owners:"
    Check ($owners.Count -and $owners[0] -notmatch "partial_patch") "failed mod's patches are removed"
    Check ($owners.Count -and $owners[0] -match "mod\.onehalf\.skiploading") "working mods' patches remain"
    if ($isSteam) {
        Check (($log -match "\[test\.steam_blocked\] .*Leaderboard.*: BLOCKED").Count -eq 6) "all 6 leaderboard calls are blocked"
        Check ($log -match "\[test\.steam_blocked\] SetAchievement: reached Steam").Count "achievements stay on by default"
    }
}

foreach ($gameDir in $GameDirs) {
    if (Test-Path (Join-Path $gameDir "Last Call BBS.exe")) { Test-Build $gameDir }
    else { Write-Host "`nSkipping $gameDir (not found)" }
}

Write-Host ""
if ($script:failed -eq 0) { Write-Host "All checks passed." -ForegroundColor Green; exit 0 }
Write-Host "$($script:failed) check(s) failed." -ForegroundColor Red
exit 1
