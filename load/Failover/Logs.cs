using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Failover;

/// <summary>
/// The logs that tell where the time of a switchover goes, followed from the start of the load: the operator of
/// CloudNativePG (when it decided, stopped the primary and promoted a replica), the instances of PostgreSQL (how long
/// the checkpoint of the shutdown took, when the promotion ended) and Payments.Api (when its writes failed and came
/// back). They are followed rather than read afterwards: the evicted primary is a new pod by then, and its log is gone.
/// Dispose stops them and writes switchover.md, the lines that matter in the order of their time.
/// </summary>
public sealed partial class LogStreams : IDisposable
{
    private readonly string reports;
    private readonly List<(string File, LongProcess Process)> streams = [];
    private bool disposed;

    public LogStreams(string reports)
    {
        this.reports = reports;
        Follow("cnpg-operator.log", "-n", "cnpg-system", "-l", "app.kubernetes.io/name=cloudnative-pg");
        Follow("postgres.log", "-n", Cluster.Namespace, "-l", "cnpg.io/cluster=pg,cnpg.io/podRole=instance", "-c", "postgres");
        // The sidecar of the Barman Cloud plugin: it archives WAL to the object store and restores it, which the
        // shutdown of the primary and the promotion of a replica both wait for.
        Follow("barman-cloud.log", "-n", Cluster.Namespace, "-l", "cnpg.io/cluster=pg,cnpg.io/podRole=instance", "-c", "plugin-barman-cloud");
        Follow("payments-api.log", "-n", Cluster.Namespace, "-l", "app.kubernetes.io/name=payments-api");
    }

    private void Follow(string file, params string[] selector)
    {
        string[] arguments = ["logs", .. selector, "-f", "--tail=0", "--timestamps", "--prefix", "--max-log-requests=10"];
        streams.Add((file, new LongProcess("kubectl", arguments, Path.Combine(reports, file))));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        foreach (var (_, process) in streams)
            process.Dispose();
        try
        {
            WriteTimeline();
        }
        catch (IOException e)
        {
            Console.WriteLine($"Could not write switchover.md: {e.Message}");
        }
    }

    // A line of kubectl logs --prefix --timestamps: [pod/name/container] 2026-11-16T10:00:00.123456789Z text.
    [GeneratedRegex(@"^\[pod/(?<pod>[^/\]]+)/[^\]]*\]\s+(?<at>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z)\s(?<text>.*)$")]
    private static partial Regex LineFormat();

    // What a switchover is made of. The operator and PostgreSQL write JSON; matching the words is enough here.
    [GeneratedRegex(@"switchover|promot|demot|fast shutdown|smart shutdown|shutting down|shut down|checkpoint (starting|complete)|" +
                    @"database system is ready|archiv|restor|timeline|history|current primary|target primary|setting.*primary|fenc|evict|drain|unschedulable",
        RegexOptions.IgnoreCase)]
    private static partial Regex PgEvents();

    // Payments.Api: errors of the base, and the first good answer after them is seen in the samples instead.
    [GeneratedRegex(@"Npgsql|PostgresException|read-only|57P01|57P03|25006|recovery|terminating connection", RegexOptions.IgnoreCase)]
    private static partial Regex ApiEvents();

    // CNPG writes JSON: the message is in msg, or in record.message for a line of PostgreSQL itself.
    private static string Message(string text)
    {
        if (!text.StartsWith('{'))
            return text.Trim();
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.TryGetProperty("record", out var record) && record.TryGetProperty("message", out var message))
                return "postgres: " + message.GetString();
            return root.TryGetProperty("msg", out var msg) ? msg.GetString() ?? text : text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private void WriteTimeline()
    {
        var events = new List<(DateTimeOffset At, string Pod, string Text)>();
        foreach (var (file, _) in streams)
        {
            var path = Path.Combine(reports, file);
            if (!File.Exists(path))
                continue;
            var filter = file == "payments-api.log" ? ApiEvents() : PgEvents();
            var lastApi = new Dictionary<string, DateTimeOffset>();
            foreach (var line in File.ReadLines(path))
            {
                var match = LineFormat().Match(line);
                if (!match.Success)
                    continue;
                var text = Message(match.Groups["text"].Value);
                if (!filter.IsMatch(text) || text.Contains("switchover or a failover in progress", StringComparison.Ordinal)
                    || text == "postgres: the database system is shutting down")
                    continue;
                var at = DateTimeOffset.Parse(match.Groups["at"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                var pod = match.Groups["pod"].Value;

                // Payments.Api fails every request while the base is away: one line a second per pod is enough.
                if (file == "payments-api.log")
                {
                    if (lastApi.TryGetValue(pod, out var last) && at - last < TimeSpan.FromSeconds(1))
                        continue;
                    lastApi[pod] = at;
                }
                events.Add((at, pod, text.Length > 300 ? text[..300] + "…" : text));
            }
        }

        var markdown = new StringBuilder("# Switchover по логам\n\n");
        markdown.Append("Строки оператора CNPG, PostgreSQL и Payments.Api, которые относятся к переключению, по времени. ");
        markdown.Append("Время — от первой строки. Полные логи — `cnpg-operator.log`, `postgres.log`, `barman-cloud.log`, `payments-api.log`.\n\n");
        if (events.Count == 0)
        {
            markdown.Append("Подходящих строк нет.\n");
        }
        else
        {
            var first = events.Min(e => e.At);
            markdown.Append("| Время | Под | Строка |\n|---|---|---|\n");
            foreach (var (at, pod, text) in events.OrderBy(e => e.At))
                markdown.Append($"| +{(at - first).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} с | `{pod}` | `{text.Replace("|", "\\|").Replace("`", "'")}` |\n");
        }
        File.WriteAllText(Path.Combine(reports, "switchover.md"), markdown.ToString());
    }
}
