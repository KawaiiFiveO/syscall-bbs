# Edits a Food Court solution as text while the game is running. Each time the text file is saved,
# it's checked, and if it's valid it replaces the solution in the game. The game must be running
# through the mod loader, with FoodCourtTools installed.
#
#   tools\fcedit.ps1 <solution file or name, or text or .fcn file> [-Netlist] [-Run] [-Editor <command>] [-NoEditor]
#   tools\fcedit.ps1 <text or .fcn file> -Once [-Run]
#
# A solution (wine-oclock-2 or its .solution path) is exported to mods\FoodCourtTools\text\ and
# opened in an editor, as text, or as a netlist with -Netlist. A text or .fcn file is used as it is.
# If no solution has the file's name, the game makes a new one. -Once checks and applies the file
# once, then exits; exit code 0 means it was applied or is waiting for the puzzle screen.
#
# A .fcn netlist is compiled on every save and its cost report printed (-Strict and -NoOptimise pass
# --strict and -O0 to the compiler). -Run runs each applied version and prints its score.
#
# The edit is one undo step in the game: Ctrl+Z in the puzzle screen reverts it.
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Path,
    [string]$Editor,
    [switch]$NoEditor,
    [switch]$Once,
    [switch]$Netlist,
    [switch]$Run,
    [switch]$Strict,
    [switch]$NoOptimise
)
$ErrorActionPreference = "Stop"

$process = Get-Process -Name "Last Call BBS (Modded)" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { throw "The game isn't running through the mod loader." }
$mod = Join-Path (Split-Path $process.Path) "mods\FoodCourtTools"
if (-not (Test-Path $mod)) { throw "FoodCourtTools isn't installed. Use install.ps1 -WithFoodCourtTools." }
$edits = Join-Path $mod "edits"
$folderFile = Join-Path $edits "solutions-folder.txt"
if (-not (Test-Path $folderFile)) { throw "FoodCourtTools hasn't started live editing yet. Wait for the game to finish loading, or check its log." }
$solutions = (Get-Content $folderFile -Raw).Trim()

$fctext = Join-Path $PSScriptRoot "fctext\bin\Release\net472\fctext.exe"
if (-not (Test-Path $fctext)) {
    dotnet build (Join-Path $PSScriptRoot "fctext\fctext.csproj") -c Release -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not build fctext" }
}
$work = Join-Path $env:TEMP "fcedit"
New-Item -ItemType Directory -Force $work | Out-Null

# Runs fctext and returns its exit code and output. Reading stderr this way keeps
# PowerShell 5.1 from turning it into errors.
function Invoke-Fctext([string[]]$arguments) {
    $info = New-Object System.Diagnostics.ProcessStartInfo $fctext
    $info.Arguments = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join " "
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($info)
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    return @{ Code = $p.ExitCode; Out = $out; Err = $err }
}

function Read-Bytes([string]$file) {
    try { return [IO.File]::ReadAllBytes($file) } catch [IO.IOException] { return $null }
}

function Get-Hash([byte[]]$bytes) {
    if ($null -eq $bytes) { return $null }
    $sha = [Security.Cryptography.SHA256]::Create()
    return [BitConverter]::ToString($sha.ComputeHash($bytes))
}

# The solution as text without the lines a save can change on its own, for telling real changes apart.
function Get-Layout([string]$solutionFile) {
    $result = Invoke-Fctext @($solutionFile)
    if ($result.Code -ne 0) { return $null }
    return (($result.Out -split "`n") | Where-Object { $_ -notmatch '^(solved|unsolved)' }) -join "`n"
}

function Write-Status([string]$message, [string]$color = "Gray") {
    Write-Host "[$(Get-Date -Format HH:mm:ss)] $message" -ForegroundColor $color
}

function Test-Netlist([string]$file) { return $file -like "*.fcn" }

function Export-Text([string]$solutionFile, [string]$textFile) {
    $arguments = if (Test-Netlist $textFile) { @("--to-netlist", $solutionFile, "-o", $textFile) } else { @($solutionFile, "-o", $textFile) }
    $result = Invoke-Fctext $arguments
    if ($result.Code -ne 0) { throw "Could not export ${solutionFile}: $($result.Err.Trim())" }
}

# Compiles a netlist and prints its report. Returns the .solution path, or $null after printing the errors.
function Test-NetlistFile([string]$netlistFile, [string]$candidate) {
    $arguments = @("--compile", $netlistFile, $candidate)
    if ($Strict) { $arguments += "--strict" }
    if ($NoOptimise) { $arguments += "-O0" }
    $compile = Invoke-Fctext $arguments
    $messages = @($compile.Err.Trim() -split "`r?`n" | Where-Object { $_ } | ForEach-Object { $_.Replace($netlistFile + ': ', '') })
    if ($compile.Code -ne 0) {
        Write-Status "Not applied. The netlist has problems:" Red
        foreach ($line in $messages) { Write-Host "  $line" }
        return $null
    }
    foreach ($line in ($compile.Out.Trim() -split "`r?`n" | Where-Object { $_ -match '^  ' })) { Write-Host $line }
    foreach ($line in $messages) { Write-Host "  $line" -ForegroundColor Yellow }
    return $candidate
}

# Runs the game's version of the solution and prints T, cost, S (sum of order ticks minus one each) and W (wires).
function Show-Score([string]$solutionFile) {
    $output = & (Join-Path $PSScriptRoot "fcrun.ps1") $solutionFile -TimeoutSeconds 30 2>&1
    $json = $output | Where-Object { $_ -like "*.json" } | Select-Object -First 1
    if (-not $json -or -not (Test-Path $json)) { Write-Status "Couldn't run it: $($output -join ' ')" Yellow; return }
    $result = Get-Content $json -Raw | ConvertFrom-Json
    $wires = @((Invoke-Fctext @($solutionFile)).Out -split "`n" | Where-Object { $_ -match ' -> ' }).Count
    if ($result.status -ne "ok") {
        Write-Status "Ran it: $($result.status)$(if ($result.error) { ': ' + $result.error }), cost $($result.cost), W $wires" Red
        return
    }
    $s = ($result.orderTicks | ForEach-Object { $_ - 1 } | Measure-Object -Sum).Sum
    Write-Status "Score: $($result.ticks)T $($result.cost)k ${s}S ${wires}W (order ticks $($result.orderTicks -join ', '))" Cyan
}

# Checks the text and writes a .solution for it. Returns its path, or $null after printing the problems.
function Test-Text([string]$textFile) {
    $candidate = Join-Path $work "$([guid]::NewGuid()).solution"
    # The compiler applies the placement rules itself.
    if (Test-Netlist $textFile) { return Test-NetlistFile $textFile $candidate }
    $import = Invoke-Fctext @("--import", $textFile, $candidate)
    if ($import.Code -ne 0) {
        Write-Status "Not applied. The text has problems:" Red
        foreach ($line in ($import.Err.Trim() -split "`r?`n")) { Write-Host "  $($line.Replace($textFile + ': ', ''))" }
        return $null
    }
    $placement = Invoke-Fctext @("--check-placement", $candidate)
    if ($placement.Code -ne 0) {
        Write-Status "Not applied. The layout breaks the game's placement rules:" Red
        foreach ($line in ($placement.Out.Trim() -split "`r?`n" | Select-Object -Skip 1)) { Write-Host "  $($line.Trim())" }
        Remove-Item $candidate
        return $null
    }
    return $candidate
}

function Send-Edit([string]$candidate, [string]$target) {
    # The game can report an edit applied before it has written the file, so -Run scores this copy.
    $script:lastSent = Join-Path $work "$target.sent.solution"
    Copy-Item -Force $candidate $script:lastSent
    $result = Join-Path $edits "$target.json"
    if (Test-Path $result) { Remove-Item $result }
    # The name ending in .tmp hides the file from the mod until it's complete.
    $temporary = Join-Path $edits "$target.tmp"
    Move-Item -Force $candidate $temporary
    Move-Item -Force $temporary (Join-Path $edits "$target.solution")
    return $result
}

# Reads the mod's answer: $null if there's none yet.
function Read-Result([string]$resultFile) {
    if (-not (Test-Path $resultFile)) { return $null }
    try { return Get-Content $resultFile -Raw | ConvertFrom-Json } catch { return $null }
}

# Work out the text file and the solution it edits.
if ($Path -like "*.solution" -or -not (Test-Path $Path -PathType Leaf)) {
    $solutionFile = if ($Path -like "*.solution") { (Resolve-Path $Path).Path } else { Join-Path $solutions "$Path.solution" }
    if (-not (Test-Path $solutionFile)) { throw "No solution at $solutionFile" }
    $target = [IO.Path]::GetFileNameWithoutExtension($solutionFile)
    $textDir = Join-Path $mod "text"
    New-Item -ItemType Directory -Force $textDir | Out-Null
    $extension = if ($Netlist) { ".fcn" } else { ".txt" }
    $textFile = Join-Path $textDir "$target$extension"
    if ($Once) { throw "-Once takes a text or .fcn file." }
    # Always start from the game's current version. Earlier text that differs is kept as a backup.
    $fresh = Join-Path $work "$target$extension"
    Export-Text $solutionFile $fresh
    if ((Test-Path $textFile) -and (Get-Hash (Read-Bytes $textFile)) -ne (Get-Hash (Read-Bytes $fresh))) {
        Copy-Item -Force $textFile "$textFile.bak"
        Write-Status "Kept your earlier text as $textFile.bak"
    }
    Move-Item -Force $fresh $textFile
}
else {
    $textFile = (Resolve-Path $Path).Path
    $target = [IO.Path]::GetFileNameWithoutExtension($textFile)
}

if ($Once) {
    $candidate = Test-Text $textFile
    if (-not $candidate) { exit 1 }
    $resultFile = Send-Edit $candidate $target
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $answer = Read-Result $resultFile
        if ($answer) {
            if ($answer.status -eq "applied") {
                Write-Status "Applied to $($answer.file)." Green
                if ($Run) { Show-Score $script:lastSent }
                exit 0
            }
            if ($answer.status -eq "waiting") { Write-Status "Waiting: $($answer.message). It applies when the puzzle is idle." Yellow; exit 0 }
            Write-Status "The game refused it: $($answer.message)" Red
            exit 1
        }
        Start-Sleep -Milliseconds 100
    }
    throw "No answer from the game after 15 seconds."
}

if (-not $NoEditor) {
    $command = if ($Editor) { $Editor } elseif ($env:EDITOR) { $env:EDITOR } elseif (Get-Command code -ErrorAction SilentlyContinue) { "code" } else { "notepad" }
    $resolved = Get-Command $command -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $resolved) { throw "Editor not found: $command" }
    Start-Process -FilePath $resolved.Source -ArgumentList "`"$textFile`"" | Out-Null
}

Write-Status "Editing $textFile"
Write-Status "Save the file to apply it in the game. Ctrl+C stops."

$gameFile = Join-Path $solutions "$target.solution"
$lastText = Get-Hash (Read-Bytes $textFile)
$textSynced = $true                              # the text matches the game's version
$lastLayout = if (Test-Path $gameFile) { Get-Layout $gameFile } else { $null }
$lastGame = if (Test-Path $gameFile) { Get-Hash (Read-Bytes $gameFile) } else { $null }
$pending = $null                                 # result file of an edit not yet answered
$sentAt = $null
$reportedWaiting = $false

while ($true) {
    Start-Sleep -Milliseconds 250

    $textHash = Get-Hash (Read-Bytes $textFile)
    if ($textHash -and $textHash -ne $lastText) {
        $lastText = $textHash
        $textSynced = $false
        $candidate = Test-Text $textFile
        if ($candidate) {
            $pending = Send-Edit $candidate $target
            $sentAt = Get-Date
            $reportedWaiting = $false
            Write-Status "Valid. Sent to the game."
        }
    }

    if ($pending) {
        $answer = Read-Result $pending
        if ($answer -and $answer.status -eq "waiting") {
            if (-not $reportedWaiting) { Write-Status "Waiting: $($answer.message). It applies when the puzzle is idle." Yellow }
            $reportedWaiting = $true
        }
        elseif ($answer -and $answer.status -eq "applied") {
            Write-Status "Applied to $($answer.file)$(if ($answer.open) { ', in the open puzzle' }). Ctrl+Z in the game undoes it." Green
            if ($answer.file -ne $target) {
                $target = $answer.file
                $gameFile = Join-Path $solutions "$target.solution"
                Write-Status "The game saved it as a new solution, $target. Later saves update that one."
            }
            $pending = $null
            $textSynced = $true
            $lastGame = Get-Hash (Read-Bytes $gameFile)
            $lastLayout = Get-Layout $gameFile
            if ($Run) { Show-Score $script:lastSent }
        }
        elseif ($answer) {
            Write-Status "The game refused it: $($answer.message)" Red
            $pending = $null
        }
        elseif (-not $reportedWaiting -and ((Get-Date) - $sentAt).TotalSeconds -gt 10) {
            Write-Status "No answer from the game yet. Is it still running?" Yellow
            $reportedWaiting = $true
        }
        continue
    }

    # Changes made in the game.
    if (-not (Test-Path $gameFile)) { continue }
    $gameHash = Get-Hash (Read-Bytes $gameFile)
    if (-not $gameHash -or $gameHash -eq $lastGame) { continue }
    $lastGame = $gameHash
    $layout = Get-Layout $gameFile
    if ($null -eq $layout -or $layout -eq $lastLayout) { continue }
    $lastLayout = $layout
    if (Test-Netlist $textFile) {
        # Re-exporting would replace your names with generic ones, so the game's version goes next to it.
        $mirror = [IO.Path]::ChangeExtension($textFile, ".game.fcn")
        Export-Text $gameFile $mirror
        Write-Status "Changed in the game. Its version is in $mirror; your netlist is unchanged." Yellow
    }
    elseif ($textSynced) {
        Export-Text $gameFile $textFile
        $lastText = Get-Hash (Read-Bytes $textFile)
        Write-Status "Changed in the game. The text file now matches it."
    }
    else {
        Write-Status "Changed in the game, and your text has changes not yet applied. Your next valid save replaces the game's version; Ctrl+Z in the game brings it back." Yellow
    }
}
