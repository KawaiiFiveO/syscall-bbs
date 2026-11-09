# Runs a Food Court solution in the game and waits for the result. The game must be running
# through the mod loader, with FoodCourtTools installed.
#
#   tools\fcrun.ps1 <file.solution or text file>
#
# Prints the status, then the path of the full JSON result. Exit code 0 means the run finished
# without an error.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$TimeoutSeconds = 30
)
$ErrorActionPreference = "Stop"

$process = Get-Process -Name "Last Call BBS (Modded)" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { throw "The game isn't running through the mod loader." }
$mod = Join-Path (Split-Path $process.Path) "mods\FoodCourtTools"
if (-not (Test-Path $mod)) { throw "FoodCourtTools isn't installed. Use install.ps1 -WithFoodCourtTools." }

$name = [IO.Path]::GetFileNameWithoutExtension($Path) -replace '[^A-Za-z0-9_.-]', '_'
$id = "$name-$(Get-Date -Format yyyyMMddHHmmss)-$(Get-Random -Maximum 100000)"
$requests = Join-Path $mod "requests"

# The name ending in .tmp hides the file from the mod until it's complete.
$temporary = Join-Path $requests "$id.tmp"
if ($Path -like "*.solution") {
    Copy-Item -LiteralPath $Path $temporary
}
else {
    $fctext = Join-Path $PSScriptRoot "fctext\bin\Release\net472\fctext.exe"
    if (-not (Test-Path $fctext)) {
        dotnet build (Join-Path $PSScriptRoot "fctext\fctext.csproj") -c Release -v q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not build fctext" }
    }
    $imported = Join-Path $env:TEMP "$id.solution"
    & $fctext --import $Path $imported | Out-Null
    if ($LASTEXITCODE -ne 0) { exit 2 }
    Move-Item $imported $temporary
}
Move-Item $temporary (Join-Path $requests "$id.solution")

$result = Join-Path $mod "results\$id.json"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while (-not (Test-Path $result)) {
    if ((Get-Date) -gt $deadline) { throw "No result after $TimeoutSeconds seconds." }
    Start-Sleep -Milliseconds 100
}

$json = Get-Content $result -Raw | ConvertFrom-Json
Write-Output "status=$($json.status) ticks=$($json.ticks) cost=$($json.cost) orders=$(@($json.orderTicks).Count)"
if ($json.error) { Write-Output "error=$($json.error)" }
Write-Output $result
$waveforms = Join-Path $mod "results\$id.order*.vcd"
if (Test-Path $waveforms) { Write-Output "waveforms: $waveforms" }
if ($json.status -ne "ok") { exit 1 }
