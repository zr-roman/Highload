# Recovery drill: an operator's mistake, DELETE FROM payments on the primary, which replication carries to every
# replica within milliseconds; then point-in-time recovery of the backups into a new cluster pg-restore, up to a few
# seconds before the mistake; then the deleted rows copied back into pg. Measures the time of the recovery.
# Run without load: the row counts before and after are then exact. docs/postgres-failover.md has the results.
#   .\load\backup-drill.ps1            # pg-restore stays for a look around
#   .\load\backup-drill.ps1 -Cleanup   # pg-restore is deleted at the end
param([switch]$Cleanup)

$ns = 'highload'
function Sql([string]$pod, [string]$sql) { kubectl -n $ns exec $pod -c postgres -- psql -d payments -Atc $sql }
function Stamp([string]$text) { '{0:HH:mm:ss}  {1}' -f (Get-Date), $text }

$primary = kubectl -n $ns get pods -l 'cnpg.io/cluster=pg,cnpg.io/instanceRole=primary' -o jsonpath='{.items[0].metadata.name}'
$before = Sql $primary 'select count(*) from payments'
Stamp "primary $primary, payments: $before"

# The target: the time of the cluster, not of the laptop. The clock of WSL can drift from Windows.
$target = Sql $primary "select replace(to_char(now() at time zone 'utc', 'YYYY-MM-DD HH24:MI:SS'), ' ', 'T') || 'Z'"
Stamp "target time $target"
Start-Sleep 3

$deleted = Sql $primary 'with d as (delete from payments returning 1) select count(*) from d'
Stamp "the mistake: DELETE FROM payments, $deleted rows; left on pg: $(Sql $primary 'select count(*) from payments')"

# The WAL with the DELETE goes to the archive at the next segment switch, within archive_timeout (60 s). Recovery
# needs the WAL past the target time to know where to stop: switch now instead of waiting.
$switchedAt = Sql $primary 'select now()'
Sql $primary 'select pg_switch_wal()' | Out-Null
do {
    Start-Sleep 2
    $archived = Sql $primary "select coalesce(last_archived_time >= '$switchedAt', false) from pg_stat_archiver"
} until ($archived -eq 't')
Stamp 'the segment with the DELETE is in the archive'

$watch = [Diagnostics.Stopwatch]::StartNew()
(Get-Content "$PSScriptRoot/pg-restore.yaml" -Raw) -replace 'TARGET_TIME', $target | kubectl -n $ns apply -f - | Out-Null
Stamp 'pg-restore created'
$last = ''
do {
    Start-Sleep 5
    $phase = kubectl -n $ns get cluster pg-restore -o jsonpath='{.status.phase}' 2>$null
    if ($phase -ne $last) { Stamp ('{0,4:n0} s  {1}' -f $watch.Elapsed.TotalSeconds, $phase); $last = $phase }
} until ($phase -eq 'Cluster in healthy state')
$rto = [math]::Round($watch.Elapsed.TotalSeconds)

$restored = Sql 'pg-restore-1' 'select count(*) from payments'
Stamp "pg-restore: $restored payments of $before, recovered in $rto s"

# The repair: the rows from pg-restore back into pg, through pg-rw as the application's user. Inside the cluster,
# from the pod of pg-restore: the data does not pass through the laptop.
$password = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String((kubectl -n $ns get secret pg-app -o jsonpath='{.data.password}')))
kubectl -n $ns exec pg-restore-1 -c postgres -- bash -c "pg_dump -d payments -t payments --data-only | PGPASSWORD='$password' psql -q -h pg-rw -U app -d payments" | Out-Null
Stamp "pg after the repair: $(Sql $primary 'select count(*) from payments') payments"

if ($Cleanup) {
    kubectl -n $ns delete cluster pg-restore | Out-Null
    Stamp 'pg-restore deleted'
}
