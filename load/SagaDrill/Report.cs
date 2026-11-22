using System.Globalization;
using System.Text;

namespace SagaDrill;

/// <summary>report.md: the stand, the warm-up load, every query with what Prometheus answered, and the checks.</summary>
public static class Report
{
    // The style of the docs: a decimal comma, thousands split by a space.
    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = " " };

    public static string Markdown(DrillOptions options, string route, PreflightResult preflight, string? commit, bool dirty, DateTimeOffset started)
    {
        var text = new StringBuilder();
        text.Append("# Учения саги: предпроверка\n\n");
        text.Append($"{started:dd.MM.yyyy HH:mm}. ");
        text.Append(commit is null ? "Коммит неизвестен. " : $"Коммит `{commit}`{(dirty ? " и незакоммиченные изменения" : "")}. ");
        text.Append($"Prometheus — {route}.\n\n");

        text.Append("## Проверки\n\n");
        foreach (var check in preflight.Checks)
            text.Append($"- {(check.Ok ? "✓" : "✗")} **{check.Name}**: {check.Detail}\n");
        text.Append('\n');

        text.Append("## Прогрев\n\n");
        if (preflight.Warmup is not { } k6)
            text.Append("Не было: стенд не готов к учениям, см. проверки.\n\n");
        else
            text.Append($"k6, {options.Rate} платежей в секунду, {options.WarmupSeconds} с: запросов {N(k6.Requests)}, " +
                $"ошибок {N(k6.Failed)}, пропущенных итераций {N(k6.Dropped)}, медиана {Ms(k6.Median)}, p99 {Ms(k6.P99)}. " +
                (preflight.PaymentsApi is { } api
                    ? $"В конце прогрева готовы {api.Ready} из {api.Desired} подов Payments.Api: HPA добавляет их под нагрузкой.\n\n"
                    : "Сколько подов было у Payments.Api в конце прогрева, прочитать не удалось.\n\n"));

        if (preflight.Queries.Count > 0)
        {
            text.Append("## Запросы\n\n");
            text.Append("Запрос панели — как его отправляет Grafana, `$__rate_interval` = `1m`. Запрос алерта — его выражение без порога: " +
                "целиком у него нет серий, пока алерт не сработал.\n\n");
            text.Append("| | Что | Серий | Значения | Запрос |\n|---|---|---|---|---|\n");
            foreach (var query in preflight.Queries)
                text.Append($"| {(query.Ok ? "✓" : "✗")} | {Cell(query.What)} | {query.Result.Series.Count} | {Cell(Values(query))} | `{Cell(query.Expression)}` |\n");
            text.Append('\n');
        }
        return text.ToString();
    }

    // Up to four series, each with the labels that tell it from the others: "0,8 (path=request); 0 (path=recovery)".
    private static string Values(QueryCheck query)
    {
        if (query.Result.Error is { } error)
            return error;
        if (query.Result.Series.Count == 0)
            return query.Found is { } found ? $"— ({found})" : "—";

        var shown = query.Result.Series.Take(4).Select(s =>
        {
            var labels = string.Join(", ", s.Labels.Where(l => l.Key != "__name__").Select(l => $"{l.Key}={l.Value}"));
            return labels.Length > 0 ? $"{Value(s.Value)} ({labels})" : Value(s.Value);
        });
        var values = string.Join("; ", shown) + (query.Result.Series.Count > 4 ? "; …" : "");
        return query.Threshold is { } threshold ? $"{values}, порог {threshold}" : values;
    }

    private static string Value(double value) =>
        double.IsNaN(value) ? "NaN" : double.IsInfinity(value) ? (value > 0 ? "+Inf" : "-Inf") : value.ToString("0.###", Numbers);

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");

    private static string N(long value) => value.ToString("N0", Numbers);

    private static string Ms(double value) => double.IsNaN(value) ? "—" : $"{value.ToString("0", Numbers)} мс";
}
