using System.Globalization;
using System.Text;

namespace AntifraudBench;

/// <summary>
/// table.md of the bench, for docs/load-testing.md: the medians of the repeats of every step, then every repeat.
/// Written again after every step, so a stopped bench keeps what it has measured.
/// </summary>
public sealed class Report(string folder, BenchOptions options, string k6Image, string commit, DateTimeOffset started)
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public string Path { get; } = System.IO.Path.Combine(folder, "table.md");

    public void Write(IReadOnlyList<StepResult> steps)
    {
        var text = new StringBuilder();
        text.AppendLine($"# Антифрод под нагрузкой, {started.LocalDateTime.ToString("dd.MM.yyyy HH:mm", Russian)}");
        text.AppendLine();
        text.AppendLine("| | |");
        text.AppendLine("|---|---|");
        text.AppendLine($"| Коммит | {commit} |");
        text.AppendLine($"| Антифрод | `{Stand.Image}`, Linux-контейнер в сети docker compose рядом с Redis; " +
                        $"`ANTIFRAUD_REDIS_TIMEOUT_MS` = {options.RedisTimeoutMs}; " +
                        $"{(options.Cpus > 0 ? $"лимит процессора {Number(options.Cpus, "0.##")}" : "без лимита процессора")} |");
        text.AppendLine($"| Нагрузка | `{k6Image}` в контейнере в той же сети, [load/antifraud.js](../../antifraud.js), " +
                        $"10 000 продавцов поровну; {options.DurationSeconds} с на ступень, прогрев {options.WarmupSeconds} с после каждого запуска, " +
                        $"{options.Repeats} повтор(а) на ступень, перед каждым Redis очищается |");
        text.AppendLine("| Процессор | `docker stats` раз в секунду, медиана за ступень; 100 % — одно ядро |");
        text.AppendLine();
        text.AppendLine("Критерий этапа 4 — p99 не больше 20 мс, меньше 1 % ошибок и меньше 1 % неотправленных проверок.");
        text.AppendLine();

        text.AppendLine("## Медианы повторов");
        text.AppendLine();
        text.AppendLine("| Процессов | Нагрузка, /с | Обработано, /с | p50, мс | p95, мс | p99, мс | Ошибок | Не отправлено, /с | " +
                        "CPU антифрода | CPU Redis | CPU k6 | Байт Redis на проверку | Критерий |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var group in steps.GroupBy(step => (step.Workers, step.Rate)))
        {
            var repeats = group.ToList();
            double Median(Func<StepResult, double> value) => MedianOf(repeats.Select(value));
            var met = repeats.Count(step => step.MeetsCriterion);

            text.AppendLine(
                $"| {group.Key.Workers} | {group.Key.Rate} | {Number(Median(s => s.K6.RequestsPerSecond), "0")} | " +
                $"{Number(Median(s => s.K6.P50), "0.0")} | {Number(Median(s => s.K6.P95), "0.0")} | {Number(Median(s => s.K6.P99), "0.0")} | " +
                $"{Percent(Median(s => s.K6.FailedShare))} | {Number(Median(s => s.K6.DroppedPerSecond), "0.0")} | " +
                $"{Number(Median(s => s.AntifraudCpu.Median), "0")} % | {Number(Median(s => s.RedisCpu.Median), "0")} % | " +
                $"{Number(Median(s => s.K6Cpu.Median), "0")} % | {Number(Median(BytesPerCheck), "0")} | " +
                $"{(met == repeats.Count ? "да" : met == 0 ? "нет" : $"{met} из {repeats.Count}")} |");
        }
        text.AppendLine();

        text.AppendLine("## Каждый повтор");
        text.AppendLine();
        text.AppendLine("| Процессов | Нагрузка, /с | Повтор | Обработано, /с | p50, мс | p95, мс | p99, мс | Ошибок | Из них 503 | " +
                        "Не отправлено, /с | CPU антифрода, медиана / пик | CPU Redis | CPU k6 | Ключей в Redis | Память Redis, МБ | Пороги k6 |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var step in steps)
        {
            text.AppendLine(
                $"| {step.Workers} | {step.Rate} | {step.Repeat} | {Number(step.K6.RequestsPerSecond, "0")} | " +
                $"{Number(step.K6.P50, "0.0")} | {Number(step.K6.P95, "0.0")} | {Number(step.K6.P99, "0.0")} | " +
                $"{Percent(step.K6.FailedShare)} | {step.K6.Status503} | {Number(step.K6.DroppedPerSecond, "0.0")} | " +
                $"{Number(step.AntifraudCpu.Median, "0")} / {Number(step.AntifraudCpu.Peak, "0")} % | " +
                $"{Number(step.RedisCpu.Median, "0")} % | {Number(step.K6Cpu.Median, "0")} % | " +
                $"{step.Redis.Keys} | {Number(step.Redis.UsedBytes / 1024.0 / 1024.0, "0.0")} | " +
                $"{(step.K6.CrossedThresholds.Count == 0 ? "все выдержаны" : string.Join("; ", step.K6.CrossedThresholds))} |");
        }

        File.WriteAllText(Path, text.ToString());
    }

    // Redis holds an empty database's few hundred kilobytes before the step: the share of one check is what the step added.
    private static double BytesPerCheck(StepResult step) =>
        step.K6.Requests > 0 ? (double)step.Redis.UsedBytes / step.K6.Requests : 0;

    private static double MedianOf(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
            return 0;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static string Number(double value, string format) => value.ToString(format, Russian);

    private static string Percent(double share) => share < 0.0005 && share > 0
        ? "< 0,1 %"
        : (share * 100).ToString("0.#", Russian) + " %";
}
