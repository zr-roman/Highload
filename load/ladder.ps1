# Step load test: runs load/payments.js at increasing rates and prints the ledger invariants after each step,
# then the lag of the ledger's consumer group by partition.
# From the repository root:
#   powershell -ExecutionPolicy Bypass -File load\ladder.ps1
#   powershell -ExecutionPolicy Bypass -File load\ladder.ps1 -Rates "2000,2500,3000" -Duration 120s
#   powershell -ExecutionPolicy Bypass -File load\ladder.ps1 -Rates "1000,1500" -HotShare 0.9 -Merchants 50
param(
    [string] $Rates = "1000,1500,2000,2500,3000",
    [string] $Duration = "60s",
    # An unmeasured run before the first step, so the first step does not pay for the services' cold start:
    # JIT, the pool of database connections, the Kafka producer. "0" skips it.
    [string] $Warmup = "20s",
    # A hot merchant (stage 3): the share of the payments it gets, from 0 to 1. Empty: the stage 1 mix of merchants.
    # A string, so the value reaches k6 as typed; payments.js checks it.
    [string] $HotShare = "",
    [int] $HotMerchant = 1,
    # How many merchants get payments; 0 leaves the default of payments.js.
    [int] $Merchants = 0
)

# A string rather than int[]: with -File, "2000,2500" arrives as one argument, so the list is split here.
$steps = @($Rates -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { [int]$_ })
$testScript = Join-Path $PSScriptRoot "payments.js"
$reports = Join-Path $PSScriptRoot "reports"
New-Item -ItemType Directory -Force -Path $reports | Out-Null

# The mix of merchants, the same for the warm-up and every step: only the rate changes.
$mix = @()
if ($HotShare) { $mix += "-e", "HOT_SHARE=$HotShare", "-e", "HOT_MERCHANT=$HotMerchant" }
if ($Merchants -gt 0) { $mix += "-e", "MERCHANTS=$Merchants" }
# Runs with a hot merchant get reports of their own instead of overwriting the stage 1 ones.
$reportSuffix = if ($HotShare) { "-hot-$HotShare" } else { "" }

if ($Warmup -notin @("", "0", "0s")) {
    # At the first rate, but not above 1000/s (below this stand's knee): a warm-up past the limit
    # would leave a backlog in the outbox and in Kafka for the first measured step.
    $warmupRate = [Math]::Min($steps[0], 1000)
    Write-Host "Warm-up: RATE=$warmupRate for $Warmup, not measured"
    $env:K6_WEB_DASHBOARD = "false"
    # The summary goes to stdout and is discarded; the progress bar is on stderr and stays visible.
    # No thresholds: an unmeasured run has nothing to pass, and k6 would print their failure to stderr.
    k6 run --no-thresholds -e RATE=$warmupRate -e DURATION=$Warmup @mix $testScript | Out-Null
}

# k6 web dashboard: the charts of every step are saved to an HTML report. The live page is off (port -1):
# k6 does not exit while a browser tab with it is open, and the ladder would stall at the end of a step.
$env:K6_WEB_DASHBOARD = "true"
$env:K6_WEB_DASHBOARD_PORT = "-1"
$env:K6_WEB_DASHBOARD_PERIOD = "2s"

for ($i = 0; $i -lt $steps.Count; $i++) {
    $rate = $steps[$i]
    $env:K6_WEB_DASHBOARD_EXPORT = Join-Path $reports "rate-$rate$reportSuffix.html"

    k6 run -e RATE=$rate -e DURATION=$Duration @mix $testScript
    Invoke-RestMethod http://localhost:5200/invariants | Out-Host
    # The lag of every partition as the broker sees it, from the committed offsets (README, step 7):
    # at the end of a step it shows whether the ledger keeps up and which partition falls behind first.
    docker exec payments-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group ledger

    if ($i -lt $steps.Count - 1) {
        Read-Host "RATE=$rate done. Enter: next step, Ctrl+C: stop" | Out-Null
    }
}
