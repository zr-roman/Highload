# A node of the k3d cluster goes down under load: what k6 sees, how many attempts Traefik made per status code,
# and when Kubernetes noticed the node and moved the pod.
#
#   .\load\node-failure.ps1                                  # agent-1, 200 payments a second, log node-failure.log
#   .\load\node-failure.ps1 -Log node-failure-4.log
#
# Before the run it puts one replica of Payments.Api on the node that goes down, if none is there.
# The node is stopped 30 s into the load and started again 90 s later; the load lasts 150 s.

param(
    [string]$Node = "k3d-highload-agent-1",
    [int]$Rate = 200,
    [string]$Log = "node-failure.log"
)

$ErrorActionPreference = "Stop"
$ns = "highload"
$app = "app.kubernetes.io/name=payments-api"

function Get-PaymentsPods {
    kubectl -n $ns get pods -l $app -o json | ConvertFrom-Json | Select-Object -ExpandProperty items |
        Where-Object { -not $_.metadata.deletionTimestamp } |
        ForEach-Object { [pscustomobject]@{ Name = $_.metadata.name; Node = $_.spec.nodeName } }
}

# Traefik's counters of attempts to Payments.Api by status code. They count attempts, not requests:
# an attempt that failed and was retried on another pod is counted too.
function Get-TraefikCounts {
    $text = (Invoke-WebRequest http://localhost:9100/metrics -UseBasicParsing).Content
    $counts = @{}
    foreach ($line in $text -split "`n") {
        if ($line -match '^traefik_service_requests_total\{code="(\d+)",[^}]*service="highload-payments-api[^"]*"\}\s+(\S+)') {
            $counts[$Matches[1]] = [double]$Matches[2]
        }
    }
    $counts
}

# 1. One replica on the node that goes down: otherwise stopping it does not touch the service.
$pods = Get-PaymentsPods
if (-not ($pods | Where-Object Node -eq $Node)) {
    $other = $pods | Where-Object { $_.Node -ne "k3d-highload-server-0" } | Select-Object -First 1
    if (-not $other) { $other = $pods | Select-Object -First 1 }
    Write-Host "Moving $($other.Name) from $($other.Node) to $Node"
    # Every other node closed to new pods for a moment: the replacement can only start on $Node.
    # Cordon does not touch the pods already running there.
    $cordoned = kubectl get nodes -o name | ForEach-Object { $_ -replace '^node/', '' } | Where-Object { $_ -ne $Node }
    foreach ($n in $cordoned) { kubectl cordon $n | Out-Null }
    try {
        kubectl -n $ns delete pod $other.Name | Out-Null
        kubectl -n $ns rollout status deployment/payments-api --timeout=120s | Out-Null
    } finally {
        foreach ($n in $cordoned) { kubectl uncordon $n | Out-Null }
    }
}
Get-PaymentsPods | Format-Table -AutoSize | Out-Host
kubectl -n kube-system get pods -l k8s-app=kube-dns -o wide | Out-Host
if (-not (Get-PaymentsPods | Where-Object Node -eq $Node)) { throw "No replica of Payments.Api on $Node" }

# 2. Traefik's metrics: port-forward for the length of the run.
$metrics = Start-Job { kubectl -n kube-system port-forward deploy/traefik 9100:9100 }
try {
    Start-Sleep 3
    $before = Get-TraefikCounts

    # 3. The node goes down 30 s into the load and comes back 90 s later; the watch logs nodes and pods every 5 s.
    $outage = Start-Job -ArgumentList $Node {
        param($n)
        Start-Sleep 30; docker stop $n | Out-Null; "stopped  $(Get-Date -Format HH:mm:ss)"
        Start-Sleep 90; docker start $n | Out-Null; "started  $(Get-Date -Format HH:mm:ss)"
    }
    $watch = Start-Job -ArgumentList $ns, $app {
        param($ns, $app)
        1..30 | ForEach-Object {
            "---- $(Get-Date -Format HH:mm:ss)"
            kubectl get nodes --no-headers
            kubectl -n $ns get pods -l $app -o wide --no-headers
            Start-Sleep 5
        }
    }
    "load from $(Get-Date -Format HH:mm:ss)" | Out-Host
    k6 run -e BASE_URL=http://localhost:8080 -e RATE=$Rate -e DURATION=150s load/payments.js

    Receive-Job $outage -Wait | Out-Host
    Remove-Job $outage
    Receive-Job $watch -Wait | Out-File $Log -Encoding utf8
    Remove-Job $watch

    # 4. Attempts per status code during the run.
    $after = Get-TraefikCounts
    "Traefik, attempts to Payments.Api during the run:" | Out-Host
    $after.Keys | Sort-Object | ForEach-Object {
        [pscustomobject]@{ Code = $_; Attempts = $after[$_] - ($(if ($before.ContainsKey($_)) { $before[$_] } else { 0 })) }
    } | Where-Object Attempts -gt 0 | Format-Table -AutoSize | Out-Host
    "Timeline: $Log" | Out-Host
} finally {
    Stop-Job $metrics; Remove-Job $metrics
}
