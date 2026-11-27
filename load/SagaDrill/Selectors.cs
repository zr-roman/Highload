using System.Text.RegularExpressions;

namespace SagaDrill;

/// <summary>A metric a query selects, and the labels it filters it on.</summary>
public sealed record Selector(string Metric, IReadOnlyList<string> Labels);

/// <summary>
/// For a query that came back empty: what Prometheus has of the metrics it selects. That is what was done by hand
/// when the panel of the antifraud's latency said «No data»: count the series by the labels the query filters on.
/// </summary>
public static partial class Selectors
{
    public static async Task<string> FoundAsync(Prometheus prometheus, string expression, CancellationToken ct)
    {
        var found = new List<string>();
        foreach (var selector in Parse(expression))
        {
            var by = selector.Labels.Count > 0 ? $" by ({string.Join(", ", selector.Labels)})" : "";
            var result = await prometheus.QueryAsync($"count{by} ({selector.Metric})", ct);

            if (result.Error is { } error)
                found.Add($"`{selector.Metric}`: {error}");
            else if (result.Series.Count == 0)
                found.Add($"метрики `{selector.Metric}` в Prometheus нет");
            else
            {
                var sets = result.Series.Take(5).Select(s => selector.Labels.Count == 0
                    ? $"{s.Value} серий"
                    : "{" + string.Join(", ", selector.Labels.Select(l => $"{l}=\"{s.Labels.GetValueOrDefault(l, "")}\"")) + "}");
                found.Add($"`{selector.Metric}` есть: {string.Join(", ", sets)}{(result.Series.Count > 5 ? ", …" : "")}");
            }
        }

        return found.Count > 0 ? string.Join("; ", found) : "в запросе не найдено ни одной метрики";
    }

    /// <summary>
    /// The metrics of a query: a name right before a label matcher "{" or a range "[". Each one once, with the
    /// labels it is filtered on; a function, an aggregation or "by (le)" is followed by "(" and is not taken.
    /// </summary>
    public static IReadOnlyList<Selector> Parse(string expression)
    {
        var selectors = new List<Selector>();
        foreach (Match match in Metric().Matches(expression))
        {
            var labels = LabelName().Matches(match.Groups["matchers"].Value).Select(l => l.Groups["label"].Value)
                .Distinct().Order(StringComparer.Ordinal).ToList();

            var selector = new Selector(match.Groups["metric"].Value, labels);

            if (!selectors.Any(s => s.Metric == selector.Metric && s.Labels.SequenceEqual(selector.Labels)))
                selectors.Add(selector);
        }

        return selectors;
    }

    [GeneratedRegex(@"(?<metric>[a-zA-Z_:][a-zA-Z0-9_:]*)\s*(?:\{(?<matchers>[^}]*)\}|(?=\[))")]
    private static partial Regex Metric();

    [GeneratedRegex(@"(?<label>[a-zA-Z_][a-zA-Z0-9_]*)\s*(?:=~|!~|!=|=)")]
    private static partial Regex LabelName();
}
