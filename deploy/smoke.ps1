# Smoke test of the k3d stand: one payment through the whole path, as a client sees it, in a few seconds. Ready pods
# say only that /health/live answers: a wrong variable, a NetworkPolicy that keeps Payments.Api from the antifraud or
# a wrong address of Redis pass "rollout status" and show only under load. Here one payment must go through the bank,
# the antifraud and PostgreSQL (201 captured), its repeat must get the same payment, and the Ledger must get it
# through the outbox and Kafka, with the money still adding up.
#
#   .\deploy\smoke.ps1                  # after a push of manifests, once Argo CD has applied it
#   .\deploy\release.ps1                # runs it after the rollout
#
# The payment is 10 roubles from a merchant of its own, 999998: no load run uses it, so the balances and reports of
# the others stay as they are. The bank declines a share of payments at random: a declined one is tried again with
# a new key, up to three times.

param(
    [string]$BaseUrl = "http://localhost:8080",
    [long]$MerchantId = 999998,
    [long]$Amount = 1000,
    # How long the Ledger may take to get the payment: the outbox relay and the consumer take well under a second.
    [int]$LedgerSeconds = 20
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(10)
$clock = [Diagnostics.Stopwatch]::StartNew()

# One request; the body as an object, or $null when it is empty or not JSON (a 502 page of Traefik).
function Send([string]$method, [string]$path, [string]$body = $null, [string]$key = $null) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), "$BaseUrl$path")
    if ($body) { $request.Content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, "application/json") }
    if ($key) { $request.Headers.Add("Idempotency-Key", $key) }
    try {
        $response = $http.SendAsync($request).GetAwaiter().GetResult()
    }
    catch {
        throw "Smoke: $method $path got no answer from $BaseUrl ($($_.Exception.GetBaseException().Message)). Is the cluster up?"
    }
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $json = $null
    if ($text) { try { $json = $text | ConvertFrom-Json } catch { } }
    [pscustomobject]@{ Status = [int]$response.StatusCode; Json = $json; Text = $text }
}

function Ok([string]$what) { Write-Host ("ok    {0}  ({1:N1} s)" -f $what, $clock.Elapsed.TotalSeconds) }

function Balance {
    $answer = Send GET "/balances/merchants/$MerchantId"
    if ($answer.Status -eq 404) { return 0 }  # no payment of the merchant has reached the Ledger yet
    if ($answer.Status -ne 200) { throw "Smoke: the Ledger answered $($answer.Status) to the balance of merchant ${MerchantId}: $($answer.Text)" }
    [long]$answer.Json.balance
}

# 1. The balance before: what the payment will add to.
$before = Balance

# 2. One payment through the bank, the antifraud and PostgreSQL.
$body = @{ merchantId = $MerchantId; amount = $Amount; currency = "RUB" } | ConvertTo-Json -Compress
foreach ($attempt in 1..3) {
    $key = "smoke-$([guid]::NewGuid())"
    $payment = Send POST "/payments" $body $key
    if (-not ($payment.Status -eq 201 -and $payment.Json.status -eq "declined")) { break }
    Write-Host "      the bank declined payment $($payment.Json.id), trying again"
}
if ($payment.Status -eq 202) {
    throw "Smoke: the payment is still $($payment.Json.status) (202): the bank or the antifraud did not answer in time. " +
        "kubectl -n highload logs deploy/payments-api --since=2m"
}
if ($payment.Status -ne 201 -or $payment.Json.status -ne "captured") {
    throw "Smoke: POST /payments answered $($payment.Status), want 201 captured: $($payment.Text)"
}
$id = $payment.Json.id
$fee = [long]$payment.Json.fee
Ok "payment $id captured, fee $fee"

# 3. The same request with the same key: the same payment, not a second one.
$repeat = Send POST "/payments" $body $key
if ($repeat.Status -ne 200 -or $repeat.Json.id -ne $id) {
    throw "Smoke: the repeat with the same Idempotency-Key answered $($repeat.Status), want 200 with payment ${id}: $($repeat.Text)"
}
Ok "the repeat with the same Idempotency-Key got the same payment"

# 4. The Ledger: the merchant's balance grows by the amount less the fee once the payment comes through Kafka.
$want = $before + $Amount - $fee
$deadline = (Get-Date).AddSeconds($LedgerSeconds)
while (($now = Balance) -ne $want) {
    if ((Get-Date) -gt $deadline) {
        throw "Smoke: after $LedgerSeconds s the balance of merchant $MerchantId is $now, want $want ($before + $Amount - $fee): " +
            "is the outbox relay or the Ledger stuck? kubectl -n highload logs deploy/ledger-service --since=2m"
    }
    Start-Sleep -Milliseconds 500
}
Ok "the Ledger has it: balance of merchant $MerchantId $before -> $now"

# 5. The money adds up: every payment balanced, every account matching its postings, the balances summing to zero.
$invariants = Send GET "/invariants"
if ($invariants.Status -ne 200 -or -not $invariants.Json.ok) {
    throw "Smoke: /invariants answered $($invariants.Status): $($invariants.Text)"
}
Ok "the invariants of the Ledger hold"

Write-Host ("Smoke passed in {0:N1} s" -f $clock.Elapsed.TotalSeconds)
