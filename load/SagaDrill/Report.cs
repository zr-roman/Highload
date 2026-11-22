using System.Globalization;
using System.Text;

namespace SagaDrill;

/// <summary>report.md: the checks, then each phase: its load, what Prometheus and the database said.</summary>
public static class Report
{
    // The style of the docs: a decimal comma, thousands split by a space.
    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = " " };

    public static string Markdown(
        DrillOptions options, string route, PreflightResult preflight, IReadOnlyList<PhaseRun> phases,
        string? commit, bool dirty, DateTimeOffset started)
    {
        var text = new StringBuilder();
        text.Append(options.Only == Part.Preflight ? "# Учения саги: предпроверка\n\n" : "# Учения саги\n\n");
        text.Append($"{started:dd.MM.yyyy HH:mm}. ");
        text.Append(commit is null ? "Коммит неизвестен. " : $"Коммит `{commit}`{(dirty ? " и незакоммиченные изменения" : "")}. ");
        text.Append($"Prometheus — {route}.\n\n");

        text.Append("## Проверки\n\n");
        Checks(text, "Предпроверка", preflight.Checks);
        foreach (var phase in phases)
        {
            if (phase.NotRun is { } why)
                text.Append($"{phase.Title} не запускалась: {why}.\n\n");
            else
                Checks(text, phase.Title, phase.Checks);
        }

        text.Append("## Предпроверка\n\n");
        Preflight(text, options, preflight);
        foreach (var phase in phases)
        {
            if (phase.Result is DenyResult deny)
                Deny(text, options, deny);
            else if (phase.Result is OutageResult outage)
                Outage(text, options, outage);
        }
        return text.ToString();
    }

    private static void Checks(StringBuilder text, string title, IReadOnlyList<Check> checks)
    {
        text.Append($"{title}:\n\n");
        foreach (var check in checks)
            text.Append($"- {(check.Ok ? "✓" : "✗")} **{check.Name}**: {check.Detail}\n");
        text.Append('\n');
    }

    private static void Deny(StringBuilder text, DrillOptions options, DenyResult deny)
    {
        var c = deny.Counts;
        text.Append("## Фаза «отказы»\n\n");
        text.Append($"{options.Rate} платежей в секунду, {options.DenySeconds} с, обычная смесь k6. Продавец {DenyPhase.HotMerchant} получает " +
            "около 10 % платежей — больше, чем правило частоты пропускает обычному продавцу (100 в минуту), — и его лишние платежи " +
            "антифрод отклоняет. Фаза начинается, когда платежи прогрева уже завершились; метрики — разница счётчиков до и после.\n\n");
        text.Append($"k6: запросов {N(deny.K6.Requests)}, ошибок {N(deny.K6.Failed)}, пропущенных итераций {N(deny.K6.Dropped)}, " +
            $"медиана {Ms(deny.K6.Median)}, p99 {Ms(deny.K6.P99)}.\n\n");
        text.Append("| | |\n|---|---|\n");
        text.Append($"| Платежи фазы в базе | {string.Join(", ", deny.Statuses.OrderByDescending(s => s.Value).Select(s => $"`{s.Key}` — {N(s.Value)}"))} |\n");
        text.Append($"| Решения антифрода | разрешил {N(c.Allow)}, отказал {N(c.Deny)}, нет решения {N(c.Unknown)} |\n");
        text.Append($"| Отмены | после отказа: void {N(c.VoidedOnDeny)}, void ответил 404 — {N(c.VoidNotFound)}, 409 — {N(c.VoidConflict)}; " +
            $"банк уже отменил — {N(c.BankVoided)} |\n");
        text.Append($"| Без проверки (fail open) | {N(c.FailOpen)} |\n");
        text.Append($"| Отменённые по продавцам | {(deny.VoidedByMerchant.Count == 0 ? "—" : string.Join(", ", deny.VoidedByMerchant.Select(m => $"{m.Merchant} — {N(m.Voided)}")))} |\n");
        text.Append($"| Все платежи завершены | {(deny.Settled is { } s ? $"через {s.TotalSeconds:0} с после нагрузки" : $"нет, и через {options.SettleSeconds} с")} |\n\n");
    }

    private static void Outage(StringBuilder text, DrillOptions options, OutageResult outage)
    {
        var c = outage.Counts;
        var times = outage.Times;
        double From(double moment) => moment - times.Stop;

        text.Append("## Фаза «антифрод лежит»\n\n");
        text.Append($"{options.Rate} платежей в секунду, обычная смесь k6, {options.BeforeSeconds + options.DownSeconds + options.AfterSeconds} с. " +
            $"Через {options.BeforeSeconds} с антифрод остановлен — `kubectl scale deployment/antifraud --replicas=0`, авто-синхронизация " +
            $"Argo CD на это время выключена, — через {options.DownSeconds} с возвращён. Платёж меньше {OutagePhase.Roubles(outage.FailOpenBelow)} " +
            "без решения антифрода проводится без проверки (fail open, только в запросе), остальные ждут его в `authorized`, и " +
            "восстановление проверяет их снова, когда антифрод вернулся. Время — от остановки, по часам кластера; Prometheus " +
            $"вычисляет правила раз в {Value(outage.Interval)} с. Метрики — разница счётчиков до и после фазы.\n\n");
        text.Append($"k6: запросов {N(outage.K6.Requests)}, ошибок {N(outage.K6.Failed)}, пропущенных итераций {N(outage.K6.Dropped)}, " +
            $"медиана {Ms(outage.K6.Median)}, p99 {Ms(outage.K6.P99)}.\n\n");

        text.Append("| Событие | Время |\n|---|---|\n");
        text.Append($"| Нагрузка началась | {OutagePhase.At(From(times.LoadStart))} |\n");
        text.Append("| Антифрод остановлен | 0 с |\n");
        text.Append($"| Антифрод возвращён | {OutagePhase.At(From(times.Back))} |\n");
        text.Append($"| Реплики антифрода готовы | {(times.Ready is { } ready ? OutagePhase.At(From(ready)) : "не дождались")} |\n");
        text.Append($"| Нагрузка закончилась | {OutagePhase.At(From(times.LoadEnd))} |\n\n");

        text.Append("Алерты — по сэмплам `ALERTS`: первое вычисление в pending, первое в firing; погас — на вычислении после последнего в firing.\n\n");
        text.Append("| Алерт | Pending | Firing | Погас |\n|---|---|---|---|\n");
        foreach (var alert in outage.Alerts)
            text.Append($"| `{alert.Name}` | {At(alert.Pending)} | {At(alert.Firing)} | {(alert.FiringAtEnd ? "горит" : At(alert.Resolved))} |\n");
        text.Append('\n');

        var large = outage.Large;
        text.Append("| | |\n|---|---|\n");
        text.Append($"| Платежи фазы в базе | {string.Join(", ", outage.Statuses.OrderByDescending(s => s.Value).Select(s => $"`{s.Key}` — {N(s.Value)}"))} |\n");
        text.Append($"| Решения антифрода | разрешил {N(c.Allow)}, отказал {N(c.Deny)}; нет решения: в запросе {N(c.UnknownInRequest)}, " +
            $"в восстановлении {N(c.UnknownInRecovery)} |\n");
        text.Append($"| Без проверки (fail open) | {N(c.FailOpen)} |\n");
        text.Append($"| Ждали антифрод в `authorized` | {N(c.WaitedInRequest)} платежей; восстановление застало антифрод молчащим {N(c.WaitedInRecovery)} раз |\n");
        text.Append($"| Отмены | после отказа: void {N(c.VoidedOnDeny)}, void ответил 404 — {N(c.VoidNotFound)}, 409 — {N(c.VoidConflict)}; " +
            $"банк уже отменил — {N(c.BankVoided)} |\n");
        text.Append($"| Платежи от {OutagePhase.Roubles(outage.FailOpenBelow)}, созданные пока антифрод лежал (с +{OutagePhase.ShutDown.TotalSeconds:0} с: до того его поды ещё завершались) | " +
            $"{N(large.Made)}: проведены после его возвращения — {N(large.Captured - large.CapturedBeforeBack)}, отменены — {N(large.Voided)}, " +
            $"банк отклонил — {N(large.Declined)}, в работе — {N(large.InProgress)}, проведены до возвращения — {N(large.CapturedBeforeBack)} |\n");
        text.Append($"| Дольше всех ждал в `authorized` | {(outage.LongestWait is var (seconds, at) ? $"{N(seconds)} с, в {OutagePhase.At(at)}" : "—")} |\n");
        text.Append($"| Все платежи завершены | {(outage.Settled is { } s ? $"через {s.TotalSeconds:0} с после нагрузки" : $"нет, и через {options.SettleSeconds} с")} |\n\n");
    }

    private static string At(double? seconds) => seconds is { } value ? OutagePhase.At(value) : "—";

    private static void Preflight(StringBuilder text, DrillOptions options, PreflightResult preflight)
    {
        text.Append("### Прогрев\n\n");
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
            text.Append("### Запросы\n\n");
            text.Append("Запрос панели — как его отправляет Grafana, `$__rate_interval` = `1m`. Запрос алерта — его выражение без порога: " +
                "целиком у него нет серий, пока алерт не сработал.\n\n");
            text.Append("| | Что | Серий | Значения | Запрос |\n|---|---|---|---|---|\n");
            foreach (var query in preflight.Queries)
                text.Append($"| {(query.Ok ? "✓" : "✗")} | {Cell(query.What)} | {query.Result.Series.Count} | {Cell(Values(query))} | `{Cell(query.Expression)}` |\n");
            text.Append('\n');
        }
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

    private static string N(double value) => N((long)Math.Round(value));

    private static string Ms(double value) => double.IsNaN(value) ? "—" : $"{value.ToString("0", Numbers)} мс";
}
