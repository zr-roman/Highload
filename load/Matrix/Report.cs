using System.Globalization;
using System.Text;

namespace Matrix;

/// <summary>
/// table.md for docs/load-testing.md: the median of the repeats of every run, every repeat, and the time windows
/// of the repeats for the dashboard. Written anew after each repeat, so a matrix stopped halfway keeps what it measured.
/// </summary>
public sealed class Report(string path, StandInfo stand, StandSettings settings, MatrixOptions options)
{
    // The style of docs/load-testing.md: a decimal comma, thousands split by a space.
    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = " " };

    private static readonly string[] RunHeaders = ["Платежей/с", "Доля hot", "Лимитер"];

    private readonly DateTimeOffset started = DateTimeOffset.Now;

    public string Path => path;

    public void Write(IReadOnlyList<RunResult> results)
    {
        var columns = Columns(settings);
        var text = new StringBuilder();

        text.Append("# Матрица этапа 3\n\n");
        text.Append($"{started:dd.MM.yyyy}, {started:HH:mm}–{DateTimeOffset.Now:HH:mm}. ");
        text.Append(stand.Commit is { } commit ? $"Коммит `{commit}`{(stand.UncommittedChanges ? " и незакоммиченные изменения" : "")}. " : "Коммит неизвестен. ");
        text.Append($"Прогоны: {string.Join(", ", options.Runs.Order())}; повторов — {options.Repeats}, по {options.DurationSeconds} с нагрузки; ");
        text.Append(options.WarmupSeconds > 0 ? $"прогрев — {options.WarmupSeconds} с после каждого запуска Payments.Api.\n\n" : "без прогрева.\n\n");
        text.Append(options.Fresh
            ? "Перед матрицей базы payments и ledger очищены, топик payments.captured и группа Ledger удалены (`--fresh`).\n\n"
            : "Базы — с прошлых прогонов (без `--fresh`): сколько в журнале платежей, видно в «Окнах повторов».\n\n");
        text.Append("Стенд запущен матрицей: сервисы в Release, выборка трейсов 1 %, метрики раз в секунду; ");
        text.Append(options.BankFailsHot
            ? "эмулятор банка отвечает 500 на каждую авторизацию горячего продавца, остальных обслуживает с настройками по умолчанию (`--bank-fails-hot`). "
            : "эмулятор банка с настройками по умолчанию. ");
        text.Append($"{K6.Merchants} продавцов, горячий — продавец {K6.HotMerchant}. Лимит продавца — {settings.TokensPerSecond} в секунду, всплеск {settings.Burst}. ");
        if (options.HotQuota > 0)
            text.Append($"Квота горячего продавца — {options.HotQuota} в секунду, всплеск {options.HotBurst} (`--hot-quota`). ");
        text.Append($"{stand.K6Version}. Приоритет процесса Docker — {stand.DockerPriority ?? "не проверен"}.\n\n");
        if (options.Instances > 1)
            text.Append($"Экземпляров Payments.Api — {options.Instances}, k6 шлёт им запросы по очереди. ");
        if (options.RedisPauseSeconds > 0)
            text.Append($"В середине каждого повтора Redis на паузе {options.RedisPauseSeconds} с (`CLIENT PAUSE`): все команды к нему ждут, как у зависшего Redis. ");
        if (options.Ledgers > 1)
            text.Append($"Экземпляров Ledger — {options.Ledgers}, в одной группе: Kafka делит между ними партиции. ");
        if (options.LedgerBatch > 0)
            text.Append(options.LedgerBatch == 1
                ? "Пачка Ledger — один платёж: одна SQL-команда и один коммит смещения на платёж (`--ledger-batch 1`). "
                : $"Пачка Ledger — не больше {options.LedgerBatch} платежей (`--ledger-batch`). ");
        if (options.Instances > 1 || options.RedisPauseSeconds > 0 || options.Ledgers > 1 || options.LedgerBatch > 0)
            text.Append("\n\n");

        text.Append("## Медиана повторов\n\n");
        Table(text, ["Прогон", .. RunHeaders, .. columns.Select(column => column.Header), "Пороги", "`/invariants`"],
            results.GroupBy(result => result.Run.Id).OrderBy(repeats => repeats.Key).Select(MedianRow));

        text.Append("## Все повторы\n\n");
        Table(text, ["Повтор", .. RunHeaders, .. columns.Select(column => column.Header), "Пороги", "`/invariants`"],
            results.Select(RepeatRow));

        text.Append("## Окна повторов\n\n");
        text.Append("Время — для `ledger.payment.delay` и `ledger.batch.size` с фильтром `partition` в дашборде: эти гистограммы есть только там.\n\n");
        Table(text,
            ["Повтор", "Время", "Горячая партиция", "Сообщений в секунду: горячая / прочие в среднем", "Партиции экземпляров Ledger: сообщений в секунду",
             "Самый долгий промежуток между замерами, с", "Платежей в журнале"],
            results.Select(WindowRow));

        text.Append("## Столбцы\n\n");
        text.Append("- **p99** — из итога k6; **hot 201** — только платежи горячего продавца с решением банка, без быстрых ответов 429.\n");
        text.Append("- **202** — доля ответов 202 у остальных продавцов и у горячего: банк не дал решения вовремя — таймаут, ответ 500 или открытый circuit breaker. ");
        text.Append("Такой платёж ждёт восстановления, которое спросит банк о решении позже.\n");
        text.Append("- **429 у hot** — доля ответов 429 у горячего продавца; **hot прошло** — его платежи, которые сервис принял (201 и 202), в секунду.\n");
        text.Append("- **Сверх лимита** — на сколько принятых платежей горячего продавца больше, чем пропускает его корзина токенов за прогон: ");
        text.Append($"всплеск и лимит за каждую секунду, {Count(settings.HotBurst)} + {Count(settings.HotTokensPerSecond)} × {options.DurationSeconds} = ");
        text.Append($"{Count(settings.HotBurst + (double)settings.HotTokensPerSecond * options.DurationSeconds)}.\n");
        text.Append("- **Сбоев Redis** — строк `Redis did not answer` в логах Payments.Api, всех экземпляров, за прогон: столько раз Redis не ответил лимитеру за 100 мс. ");
        text.Append("Одна строка — один сбой: лимит на это время у каждого экземпляра Payments.Api свой, в памяти, пока пробный запрос не получит ответ Redis. ");
        text.Append("До шага 4а этапа 3 лимит в такие моменты не действовал совсем, а breaker мерцал, и строк на один сбой было много. Сколько платежей прошло сверх лимита, видно по «Сверх лимита».\n");
        text.Append("- **Не отправлено** — `dropped_iterations` от числа запланированных платежей.\n");
        text.Append("- **Outbox** — возраст самой старой строки outbox, **lag** — сообщения партиции, ещё не закоммиченные Ledger; максимум замеров раз в секунду. ");
        text.Append("Горячая партиция — та, в которую за прогон пришло больше всего сообщений.\n");
        text.Append("- **Догнал за** — сколько после конца нагрузки outbox пустел, а lag всех партиций становился 0. Замер раз в секунду, поэтому «≤ 1» — догнал сразу.\n");
        text.Append($"- **Запись, пик** — быстрее всего Ledger записывал горячую партицию и весь журнал: на сколько продвинулись закоммиченные смещения за {Count(BacklogSummary.PeakWindow.TotalSeconds)} с замеров, в секунду. ");
        text.Append("Пока партиция отстаёт, это скорость её писателя; пока не отстаёт — только то, что в неё приходит.\n");
        text.Append("- **`/invariants`** — проверка журнала после повтора: двойная запись, балансы равны проводкам, сумма всех балансов — ноль. ");
        text.Append("«нет ответа» — Ledger не ответил, и у повтора проверки нет.\n");
        text.Append($"- **Самый долгий промежуток между замерами** — обычно секунда. Дольше — замер не удался или отброшен: Postgres или Kafka отвечали дольше {Count(Backlog.SlowSample.TotalSeconds)} с, ");
        text.Append("и lag в нём был бы завышен. За такой промежуток максимумы outbox и lag не видны.\n");
        text.Append("- **Платежей в журнале** — `payments` из ответа `/invariants`: журнал растёт от повтора к повтору, и проверка вместе с ним.\n");
        text.Append("- **Партиции экземпляров Ledger** — какие партиции были у каждого экземпляра в начале повтора и сколько сообщений в секунду в них пришло; горячая — жирным.\n\n");
        text.Append("Рядом с таблицей: `run-<повтор>.json` и `.txt` — итог и вывод k6, `.html` — его графики, `-backlog.csv` — замеры раз в секунду (первый — перед нагрузкой; в последних столбцах — сколько отвечали Postgres и Kafka), ");
        text.Append("`-invariants.json` — ответ `/invariants`; логи сервисов и сборки.\n");

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string[] MedianRow(IGrouping<int, RunResult> repeats) =>
        [
            repeats.Key.ToString(),
            .. RunCells(repeats.First().Run),
            .. columns.Select(column => column.Format(Median(repeats.Select(column.Value)))),
            Agreed(repeats.Select(result => (bool?)(result.K6.CrossedThresholds.Count == 0)).ToList(), "не прошли"),
            Agreed(repeats.Select(result => result.Ledger?.Ok).ToList(), "нарушены"),
        ];

        string[] RepeatRow(RunResult result) =>
        [
            result.Label,
            .. RunCells(result.Run),
            .. columns.Select(column => column.Format(column.Value(result))),
            result.K6.CrossedThresholds.Count == 0 ? "ok" : string.Join(", ", result.K6.CrossedThresholds.Select(threshold => $"`{threshold}`")),
            result.Ledger switch { null => "нет ответа", { Ok: true } => "ok", _ => "нарушены" },
        ];
    }

    /// <summary>A number of the tables: its header, its value in a repeat (null: does not apply to the run), how to print it.</summary>
    private sealed record Column(string Header, Func<RunResult, double?> Value, Func<double?, string> Format);

    private static Column[] Columns(StandSettings settings) =>
    [
        new("p99 other, мс", r => r.K6.P99Other, Milliseconds),
        new("p99 hot, мс", r => r.Run.HasHotMerchant ? r.K6.P99Hot : null, Milliseconds),
        new("p99 hot 201, мс", r => r.Run.HasHotMerchant ? r.K6.P99Hot201 : null, Milliseconds),
        new("202 у other", r => r.K6.Other202Share, Percent),
        new("202 у hot", r => r.Run.HasHotMerchant ? r.K6.Hot202Share : null, Percent),
        new("429 у hot", r => r.Run.HasHotMerchant ? (double)r.K6.HotRateLimited / r.K6.HotRequests : null, Percent),
        new("hot прошло, /с", r => r.Run.HasHotMerchant ? (double)r.HotAccepted / r.DurationSeconds : null, Count),
        new("Сверх лимита", r => r.Run.HasHotMerchant && r.Run.Limiter ? r.OverLimit(settings) : null, Count),
        new("Сбоев Redis", r => r.Run.Limiter ? r.LimiterOutages : null, Count),
        new("Не отправлено", r => (double)r.K6.Dropped / ((long)r.Run.Rate * r.DurationSeconds), Percent),
        new("Outbox, макс. возраст, с", r => r.Backlog.OutboxMaxAgeSeconds, Seconds),
        new("Lag горячей партиции, макс.", r => r.HotPartitionLagMax, Count),
        new("Lag прочих партиций, макс.", r => r.OtherPartitionsLagMax, Count),
        new("Запись горячей партиции, пик /с", r => r.HotPartitionPeakWrite, Count),
        new("Запись журнала, пик /с", r => r.Backlog.PeakWriteTotal, Count),
        new("Догнал за, с", r => r.Backlog.CaughtUpSeconds, CatchUp),
    ];

    private static string[] RunCells(MatrixRun run) =>
        [Count(run.Rate), run.HotShare.ToString("0.##", Numbers), run.Limiter ? "вкл." : "выкл."];

    private static string[] WindowRow(RunResult result) =>
    [
        result.Label,
        $"{result.K6.Started:HH:mm:ss}–{result.K6.Finished:HH:mm:ss}",
        result.HotPartition?.ToString() ?? "—",
        $"{Count(result.HotPartitionMessagesPerSecond)} / {Count(result.OtherPartitionsMessagesPerSecond)}",
        LedgerMembers(result),
        Count(result.Backlog.LongestGapSeconds),
        Count(result.Ledger?.Payments),
    ];

    // "0, 1, 2, **3**: 957; 4, 5, 6, 7: 43": the partitions of each instance and the messages a second that came to them.
    private static string LedgerMembers(RunResult result) =>
        result.LedgerMembers is { Count: > 0 } members
            ? string.Join("; ", members.Select(partitions =>
                $"{string.Join(", ", partitions.Select(p => p == result.HotPartition ? $"**{p}**" : $"{p}"))}: {Count(result.MessagesPerSecond(partitions))}"))
            : "—";

    private static void Table(StringBuilder text, IReadOnlyList<string> header, IEnumerable<string[]> rows)
    {
        text.Append("| ").AppendJoin(" | ", header).Append(" |\n");
        text.Append('|').AppendJoin('|', header.Select(_ => "---")).Append("|\n");

        foreach (var row in rows)
            text.Append("| ").AppendJoin(" | ", row).Append(" |\n");

        text.Append('\n');
    }

    private static double? Median(IEnumerable<double?> values)
    {
        var sorted = values.OfType<double>().Order().ToList();
        if (sorted.Count == 0)
            return null;

        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    // A check in the median line: ok if every repeat passed it, otherwise in how many it did not
    // and in how many there was no answer (null).
    private static string Agreed(IReadOnlyList<bool?> passed, string failed)
    {
        var notPassed = passed.Count(ok => ok == false);
        var unanswered = passed.Count(ok => ok is null);

        if (notPassed == 0 && unanswered == 0)
            return "ok";

        var notes = new List<string>();

        if (notPassed > 0)
            notes.Add($"{failed} в {notPassed} из {passed.Count}");

        if (unanswered > 0)
            notes.Add($"нет ответа в {unanswered} из {passed.Count}");

        return string.Join(", ", notes);
    }

    // "—": the number does not apply to the run. Thousands are split from five digits on, as in docs/load-testing.md:
    // 1500, but 24 400. A small median of two repeats keeps its half: 0,5 outages, not 1.
    private static string Count(double? value) =>
        value is { } v ? v.ToString(Math.Abs(v) >= 10_000 ? "#,0" : Math.Abs(v) < 10 ? "0.#" : "0", Numbers) : "—";

    // A decimal below 10 ms, as in docs/load-testing.md: 3,3 but 48.
    private static string Milliseconds(double? value) =>
        value is { } v && v < 10 ? v.ToString("0.0", Numbers) : Count(value);

    private static string Seconds(double? value) =>
        value is { } v ? v.ToString("0.0", Numbers) : "—";

    // The backlog is sampled once a second, so "caught up by the first sample after the load" is all a second or less says.
    private static string CatchUp(double? value) =>
        value is <= 1 ? "≤ 1" : Count(value);

    private static string Percent(double? share)
    {
        if (share is not { } value)
            return "—";

        var percent = value * 100;
        return percent.ToString(percent < 1 ? "0.##" : percent < 10 ? "0.#" : "0", Numbers) + " %";
    }
}
