using System.Text.Json;
using System.Text.RegularExpressions;

namespace SagaDrill;

/// <summary>A query of a panel of the dashboard, with Grafana's variables filled in as Grafana fills them.</summary>
public sealed record PanelQuery(string Panel, string Legend, string Expression);

/// <summary>The dashboard «Highload · Платежи» as it is in git: Argo CD gives Grafana the same file.</summary>
public static partial class Dashboard
{
    public const string File = "deploy/k8s/dashboards/payments.json";

    // The row of the saga: its title in payments.json starts with this.
    private const string SagaRow = "Сага";

    /// <summary>
    /// Every query of the saga row, and the queries of other panels that use a metric of the saga: «Исходы платежей»
    /// draws the voided payments too. Hidden queries are left out, as Grafana leaves them out.
    /// </summary>
    public static IReadOnlyList<PanelQuery> SagaQueries(string path)
    {
        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var queries = new List<PanelQuery>();
        var inSagaRow = false;
        foreach (var panel in json.RootElement.GetProperty("panels").EnumerateArray())
        {
            if (panel.GetProperty("type").GetString() == "row")
            {
                inSagaRow = Title(panel).StartsWith(SagaRow, StringComparison.Ordinal);
                // A collapsed row keeps its panels inside itself rather than after itself.
                if (inSagaRow && panel.TryGetProperty("panels", out var inside))
                    foreach (var nested in inside.EnumerateArray())
                        Add(nested, everyQuery: true);
                continue;
            }
            Add(panel, everyQuery: inSagaRow);
        }

        if (!queries.Any())
            throw new DrillException($"{path} has no row whose title starts with «{SagaRow}» and no query of a metric of the saga");
        return queries;

        void Add(JsonElement panel, bool everyQuery)
        {
            if (!panel.TryGetProperty("targets", out var targets))
                return;
            foreach (var target in targets.EnumerateArray())
            {
                if (target.TryGetProperty("hide", out var hide) && hide.ValueKind == JsonValueKind.True)
                    continue;
                var expression = target.GetProperty("expr").GetString()!;
                if (everyQuery || SagaMetric().IsMatch(expression))
                    queries.Add(new PanelQuery(Title(panel), Legend(target), Filled(expression)));
            }
        }
    }

    private static string Title(JsonElement panel) =>
        panel.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";

    private static string Legend(JsonElement target) =>
        target.TryGetProperty("legendFormat", out var legend) && legend.GetString() is { Length: > 0 } text
            ? text
            : target.GetProperty("refId").GetString()!;

    // Grafana's $__rate_interval is at least four scrape intervals: 1m with the 15 s of the stand. A variable the
    // tool does not know is an error, not a query sent with a dollar in it.
    private static string Filled(string expression)
    {
        var filled = expression.Replace("$__rate_interval", "1m");
        if (filled.Contains('$'))
            throw new DrillException($"A query of {File} uses a variable the tool does not fill: {expression}");
        return filled;
    }

    // The metrics of the saga (Payments.Api/Telemetry/PaymentsMetrics.cs) as Prometheus names them.
    [GeneratedRegex(@"\bpayments_(antifraud_|voided_|left_in_progress_|in_progress_)")]
    private static partial Regex SagaMetric();
}
