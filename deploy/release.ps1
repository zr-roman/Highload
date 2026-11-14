# Release of services to the k3d stand through git: build the image tagged with the commit, load it into the nodes,
# write the tag into deploy/k8s/kustomization.yaml, commit and push. Argo CD sees the new tag and rolls the pods.
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
    Invoke-Checked "docker build $image" { docker build -t $image -f $catalog[$service].Dockerfile . }
    $image
}

# 3. Into the nodes before the tag reaches git: once Argo CD sees the tag, the pods must find the image at once.
Invoke-Checked "k3d image import" { k3d image import @images -c $Cluster }

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
} else {
    Invoke-Checked "git push" { git push }
    Write-Host "Pushed. Argo CD rolls the pods within 3 minutes, at once after Refresh:"
    Write-Host "  kubectl -n highload rollout status deployment/$($Services[0])"
}
