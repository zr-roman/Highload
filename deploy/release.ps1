# Release of services to the k3d stand through git: build the image tagged with the commit, load it into the nodes,
# write the tag into deploy/k8s/kustomization.yaml, commit and push, then wait until Argo CD has applied the commit
# and the pods have rolled.
# On this stand the script stands in for CI and an image registry.
#
#   .\deploy\release.ps1 -Services payments-api                  # one service
#   .\deploy\release.ps1 -Services payments-api,ledger-service
#   .\deploy\release.ps1                                         # all four
#   .\deploy\release.ps1 -Services payments-api -NoPush          # commit only, push later
#
# The image is built from the working tree, but tagged with HEAD: so the sources of a service must be committed,
# otherwise the tag would name a commit the image does not match. The script refuses to release a service whose
# sources have uncommitted changes.

param(
    [ValidateSet("payments-api", "ledger-service", "bank-emulator", "antifraud")]
    [string[]]$Services = @("payments-api", "ledger-service", "bank-emulator", "antifraud"),
    [string]$Cluster = "highload",
    [switch]$NoPush
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# What each image is built from: its Dockerfile and the sources that go into it.
$dotnetShared = @("src/Platform", "src/Contracts", "Directory.Build.props", "Directory.Packages.props")
$catalog = @{
    "payments-api"   = @{ Dockerfile = "src/Payments.Api/Dockerfile";        Sources = @("src/Payments.Api") + $dotnetShared }
    "ledger-service" = @{ Dockerfile = "src/Ledger.Service/Dockerfile";      Sources = @("src/Ledger.Service") + $dotnetShared }
    "bank-emulator"  = @{ Dockerfile = "src/bank-emulator/Dockerfile";       Sources = @("src/bank-emulator") }
    "antifraud"      = @{ Dockerfile = "src/antifraud-service/Dockerfile";   Sources = @("src/antifraud-service") }
}

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

# 1. Committed sources only.
foreach ($service in $Services) {
    $dirty = git status --porcelain -- $catalog[$service].Sources
    if ($dirty) {
        throw "Uncommitted changes in the sources of ${service}:`n$($dirty -join "`n")`nCommit them first, or leave $service out."
    }
}
$tag = (git rev-parse --short=12 HEAD).Trim()
Write-Host "Release $($Services -join ', ') as $tag"

# 2. Build. The layers of an unchanged service come from the cache: only the tag is new.
$images = foreach ($service in $Services) {
    $image = "${service}:$tag"
    # Out-Host: the build log goes to the screen, not into $images (in PowerShell all output of the loop is its result).
    Invoke-Checked "docker build $image" { docker build -t $image -f $catalog[$service].Dockerfile . | Out-Host }
    $image
}

# 3. Into the nodes before the tag reaches git: once Argo CD sees the tag, the pods must find the image at once.
Invoke-Checked "k3d image import" { k3d image import @images -c $Cluster | Out-Host }

# 4. The tag in kustomization.yaml: the newTag line right after "- name: <service>" in the images block.
$path = "deploy/k8s/kustomization.yaml"
$text = Get-Content $path -Raw
foreach ($service in $Services) {
    $pattern = "(?m)(^\s*- name: $([regex]::Escape($service))\s*\r?\n\s*newTag:\s*)\S+"
    if ($text -notmatch $pattern) { throw "No images entry for $service in $path" }
    $text = [regex]::Replace($text, $pattern, "`${1}$tag")
}
Set-Content $path $text -NoNewline

# 5. Commit the tag alone, whatever else is staged or changed.
Invoke-Checked "git commit" { git commit -m "Release: $($Services -join ', ') $tag" -- $path }
if ($NoPush) {
    Write-Host "Committed, not pushed: git push"
    return
}
Invoke-Checked "git push" { git push }
$commit = (git rev-parse HEAD).Trim()

# 6. Wait until Argo CD has applied this very commit, then until the pods have rolled. Without the refresh Argo CD
# reads the repository every 3 minutes; the annotation is the Refresh button of its UI.
Write-Host "Pushed $($commit.Substring(0, 12)). Waiting for Argo CD to sync it"
Invoke-Checked "argocd refresh" { kubectl -n argocd annotate application highload argocd.argoproj.io/refresh=normal --overwrite | Out-Null }
$deadline = (Get-Date).AddMinutes(5)
while ($true) {
    $app = kubectl -n argocd get application highload -o json | ConvertFrom-Json
    if ($app.status.sync.revision -eq $commit -and $app.status.sync.status -eq "Synced") { break }
    if ((Get-Date) -gt $deadline) {
        throw "Argo CD has not synced $commit in 5 minutes: sync $($app.status.sync.status) at $($app.status.sync.revision)"
    }
    Start-Sleep 3
}
foreach ($service in $Services) {
    # A new pod that does not get ready fails the rollout here, not five minutes later in someone's browser.
    Invoke-Checked "rollout of $service" { kubectl -n highload rollout status deployment/$service --timeout=180s }
}
Write-Host "Released: $($Services -join ', ') $tag"
