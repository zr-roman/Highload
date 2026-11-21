# Step load test of the antifraud service alone: runs load/antifraud.js at increasing rates. Before every step Redis
# is emptied, so the steps start alike; after it, how many keys the step left and how much memory they take.
# The service and Redis must be running already (src/antifraud-service/README.md, section on running it):
#   docker compose up -d redis
#   cd src\antifraud-service; uv run uvicorn antifraud.main:app --host 127.0.0.1 --port 5400 --workers 1
# From the repository root:
#   powershell -ExecutionPolicy Bypass -File load\antifraud-ladder.ps1 -Label workers-1
#   powershell -ExecutionPolicy Bypass -File load\antifraud-ladder.ps1 -Rates "1000,1500,2000" -Label workers-4
param(
    [string] $Rates = "500,1000,1500",
    [string] $Duration = "60s",
    # An unmeasured run before the first step: the first step would otherwise pay for the cold start of the workers
    # and their pools of Redis connections. "0" skips it.
    [string] $Warmup = "15s",
    # Goes into the names of the reports: the runs with 1, 2 and 4 workers must not overwrite each other.
    [string] $Label = "run",
    [string] $BaseUrl = "http://127.0.0.1:5400",
    [string] $DenyShare = "0",
    [string] $RedisContainer = "payments-redis"
)

$ErrorActionPreference = "Stop"

# A string rather than int[]: with -File, "1000,1500" arrives as one argument, so the list is split here.
$steps = @($Rates -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { [int]$_ })
$testScript = Join-Path $PSScriptRoot "antifraud.js"
$reports = Join-Path $PSScriptRoot "reports"
New-Item -ItemType Directory -Force -Path $reports | Out-Null

# Fail at once, not after a minute of 100 % errors.
try { Invoke-RestMethod "$BaseUrl/healthz" | Out-Null }
catch { throw "Antifraud does not answer on $BaseUrl/healthz: start it first (see the header of this script)." }
docker exec $RedisContainer redis-cli ping | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Redis container $RedisContainer does not answer: docker compose up -d redis" }

function Show-Redis([string] $title) {
    $keys = docker exec $RedisContainer redis-cli dbsize
    $memory = docker exec $RedisContainer redis-cli info memory |
        Where-Object { $_ -match '^(used_memory_human|used_memory_peak_human):' } |
        ForEach-Object { $_.Trim() }
    Write-Host "${title}: keys: $keys; $($memory -join '; ')"
}

$common = @("-e", "BASE_URL=$BaseUrl", "-e", "DENY_SHARE=$DenyShare")

if ($Warmup -notin @("", "0", "0s")) {
    Write-Host "Warm-up: RATE=$($steps[0]) for $Warmup, not measured"
    $env:K6_WEB_DASHBOARD = "false"
    k6 run --no-thresholds -e RATE=$($steps[0]) -e DURATION=$Warmup @common $testScript | Out-Null
}

# The charts of every step go to an HTML report; the live page is off, as in ladder.ps1.
$env:K6_WEB_DASHBOARD = "true"
$env:K6_WEB_DASHBOARD_PORT = "-1"
$env:K6_WEB_DASHBOARD_PERIOD = "2s"

for ($i = 0; $i -lt $steps.Count; $i++) {
    $rate = $steps[$i]
    docker exec $RedisContainer redis-cli flushdb | Out-Null
    $env:K6_WEB_DASHBOARD_EXPORT = Join-Path $reports "antifraud-$Label-rate-$rate.html"

    # Thresholds that fail make k6 exit with an error; the ladder goes on, the summary already says what failed.
    k6 run -e RATE=$rate -e DURATION=$Duration @common $testScript
    Show-Redis "After RATE=$rate"

    if ($i -lt $steps.Count - 1) {
        Read-Host "RATE=$rate done. Enter: next step, Ctrl+C: stop" | Out-Null
    }
}
