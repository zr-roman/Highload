using System.Globalization;
using System.Text;

namespace Failover;

/// <summary>report.md: the timeline of the failure from the samples, what k6 and Traefik saw, and the checks.</summary>
public sealed class Report(
    FailoverOptions options, PlacementInfo placement, Outage outage, Sampler sampler, K6Result k6,
    Dictionary<string, double>? attempts, IReadOnlyList<Check> checks, string? commit, bool dirty, DateTimeOffset started)
{
    // The style of the docs: a decimal comma, thousands split by a space.
    private static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = " " };

    public string Markdown()
    {
        var text = new StringBuilder();
        text.Append($"# Отказ ноды: сценарий `{FailoverOptions.ScenarioName(options.Scenario)}`\n\n");
        text.Append($"{started:dd.MM.yyyy HH:mm}. ");
        text.Append(commit is null ? "Коммит неизвестен. " : $"Коммит `{commit}`{(dirty ? " и незакоммиченные изменения" : "")}. ");
        text.Append($"Нода `{options.NodeName}` остановлена на {options.BeforeSeconds}-й секунде нагрузки на {options.DownSeconds} с (`docker stop`, как падение машины). ");
        text.Append($"Нагрузка — {options.Rate} платежей в секунду, {options.LoadSeconds} с, через Traefik.\n\n");

        text.Append("## Перед отказом\n\n");
        text.Append($"- основной PostgreSQL — `{placement.PgPrimary}` на `{placement.PgPrimaryNode}`\n");
        text.Append($"- лидер outbox — `{placement.OutboxLeaderPod}`, на останавливаемой ноде\n");
        text.Append($"- брокер Kafka {placement.BrokerOnNode} на останавливаемой ноде, лидер {placement.PartitionsLedOnNode} партиций из 8\n");
        foreach (var move in placement.Moves)
            text.Append($"- подготовка: {move}\n");
        text.Append('\n');

        text.Append("## Хронология\n\nВремя — от остановки ноды.\n\n| Время | Событие |\n|---|---|\n");
        foreach (var (at, what) in Events())
            text.Append($"| {Since(at)} | {what} |\n");
        text.Append('\n');

        text.Append("## Пики\n\nС остановки ноды до конца замеров.\n\n| | |\n|---|---|\n");
        var stopped = outage.Stopped ?? TimeSpan.Zero;
        var samples = sampler.Samples.Where(s => s.At >= stopped).ToList();
        if (samples.Where(s => s.Outbox is not null).MaxBy(s => s.Outbox) is { } peak)
            text.Append($"| Строк в outbox, наибольшее | {N(peak.Outbox!.Value)} ({Since(peak.At)}) |\n");
        if (samples.Where(s => s.OutboxAgeSeconds is not null).MaxBy(s => s.OutboxAgeSeconds) is { } oldest)
            text.Append($"| Самое долгое ожидание в outbox | {oldest.OutboxAgeSeconds} с ({Since(oldest.At)}) |\n");
        var kafka = sampler.KafkaSamples.Where(k => k.At >= stopped).ToList();
        if (kafka.Where(k => k.Lag is not null).MaxBy(k => k.Lag) is { } lag)
            text.Append($"| Отставание Ledger, наибольшее | {N(lag.Lag!.Value)} сообщений ({Since(lag.At)}) |\n");
        if (kafka.Where(k => k.MinIsr is not null).MinBy(k => k.MinIsr) is { } isr)
            text.Append($"| Наименьший ISR | {isr.MinIsr} из 3 |\n");
        if (samples.Count > 0)
            text.Append($"| Готовых реплик Payments.Api, наименьшее | {samples.Min(s => s.ApiReady)} |\n");
        text.Append('\n');

        text.Append("## Клиенты\n\n| k6 | |\n|---|---|\n");
        text.Append($"| Запросов | {N(k6.Requests)} |\n");
        text.Append($"| Ошибок | {N(k6.Failed)} ({Percent(k6.Failed, k6.Requests)}) |\n");
        text.Append($"| Пропущенных итераций | {N(k6.Dropped)} |\n");
        text.Append($"| Медиана / p95 / p99 / максимум | {Ms(k6.Median)} / {Ms(k6.P95)} / {Ms(k6.P99)} / {Ms(k6.Max)} |\n\n");
        if (attempts is null)
            text.Append("Счётчиков Traefik нет: его под перезапускался во время прогона.\n\n");
        else
        {
            text.Append("Попытки Traefik к Payments.Api по кодам (с повторами на другой под):\n\n| Код | Попыток |\n|---|---|\n");
            foreach (var (code, count) in attempts.OrderBy(a => a.Key))
                text.Append($"| {code} | {N((long)count)} |\n");
            text.Append('\n');
        }

        text.Append("## Проверки\n\n");
        foreach (var check in checks)
            text.Append($"- {(check.Ok ? "✓" : "✗")} **{check.Name}**: {check.Detail}\n");
        text.Append("\nВсе замеры — `samples.csv`, вывод k6 — `k6.txt`, его итоги — `k6.json`.\n");
        return text.ToString();
    }

    private List<(TimeSpan At, string What)> Events()
    {
        var events = new List<(TimeSpan, string)>();
        var samples = sampler.Samples;
        var kafka = sampler.KafkaSamples;
        var stopped = outage.Stopped ?? TimeSpan.Zero;

        if (outage.Stopped is { } s)
            events.Add((s, $"`docker stop {options.NodeName}` завершился"));
        if (outage.Started is { } r)
            events.Add((r, $"`docker start {options.NodeName}`"));
        if (samples.FirstOrDefault(x => x.At > stopped && x.NodeReady == false) is { } notReady)
            events.Add((notReady.At, "нода NotReady"));
        if (outage.Started is { } back && samples.FirstOrDefault(x => x.At > back && x.NodeReady == true) is { } ready)
            events.Add((ready.At, "нода снова Ready"));

        Changes(samples, x => x.PgPrimary, "основной PostgreSQL", events);
        Changes(samples, x => x.OutboxLeader, "лидер outbox", events);
        Changes(samples, x => x.RecoveryLeader, "лидер восстановления платежей", events);

        // The outbox: when it started to grow past the normal, and when it was empty again.
        var growing = samples.FirstOrDefault(x => x.At > stopped && x.OutboxAgeSeconds > 5);
        if (growing is not null)
        {
            events.Add((growing.At, $"outbox копится: {N(growing.Outbox ?? 0)} строк, самая старая ждёт {growing.OutboxAgeSeconds} с"));
            if (samples.FirstOrDefault(x => x.At > growing.At && x.OutboxAgeSeconds <= 1) is { } drained)
                events.Add((drained.At, "outbox снова не копится"));
        }

        if (kafka.FirstOrDefault(k => k.At > stopped && k.UnderReplicated > 0) is { } shrunk)
        {
            events.Add((shrunk.At, $"Kafka: ISR сократился, недореплицировано партиций — {shrunk.UnderReplicated}"));
            if (kafka.FirstOrDefault(k => k.At > shrunk.At && k.UnderReplicated == 0) is { } full)
                events.Add((full.At, "Kafka: все партиции снова в полном ISR"));
        }
        if (placement.PartitionsLedOnNode > 0 && kafka.FirstOrDefault(k => k.At > stopped && k.LedOnNode == 0) is { } moved)
            events.Add((moved.At, $"Kafka: у партиций брокера {placement.BrokerOnNode} новые лидеры"));

        return events.OrderBy(e => e.Item1).ToList();
    }

    // Every change of a value after the first sample.
    private static void Changes(IReadOnlyList<Sample> samples, Func<Sample, string?> value, string name, List<(TimeSpan, string)> events)
    {
        // A leader lock with no holder is a state of its own, "" here; the primary missing is a probe that failed.
        var nobodyIsAState = name != "основной PostgreSQL";
        string? previous = null;
        foreach (var sample in samples)
        {
            // No answer from PostgreSQL this time (Outbox null): who holds the locks is unknown, not "nobody".
            if (nobodyIsAState && sample.Outbox is null)
                continue;
            var current = value(sample) ?? (nobodyIsAState ? "" : null);
            if (current is null)
                continue;
            if (previous is not null && current != previous)
                events.Add((sample.At, current.Length == 0 ? $"{name}: никого" : $"{name}: `{current}`"));
            previous = current;
        }
    }

    private string Since(TimeSpan at)
    {
        var seconds = (at - (outage.Stopped ?? TimeSpan.Zero)).TotalSeconds;
        return seconds >= 0 ? $"+{seconds:0} с" : $"−{-seconds:0} с";
    }

    private static string N(long value) => value.ToString("N0", Numbers);

    private static string Ms(double value) =>
        double.IsNaN(value) ? "—" : value >= 1000 ? $"{(value / 1000).ToString("0.0", Numbers)} с" : $"{value:0} мс";

    private static string Percent(long part, long whole) =>
        whole == 0 ? "—" : $"{(100.0 * part / whole).ToString("0.00", Numbers)} %";
}
