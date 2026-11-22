using System.Globalization;

namespace SagaDrill;

/// <summary>
/// The databases of the stand, through psql in the pod of the primary, as the postgres user over the local socket:
/// no password and no open port, as in load/Failover. The primary is asked for each time: a failover may move it.
/// </summary>
public static class Postgres
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The rows of a query, each split by |; null if psql failed.</summary>
    public static async Task<IReadOnlyList<string[]>?> QueryAsync(string database, string sql)
    {
        var primary = await Shell.Kubectl(Timeout, "-n", Stand.Namespace, "get", "cluster", "pg", "-o", "jsonpath={.status.currentPrimary}");
        if (!primary.Ok || primary.Output.Length == 0)
            return null;

        var result = await Shell.Kubectl(Timeout,
            "-n", Stand.Namespace, "exec", primary.Output, "-c", "postgres", "--", "psql", "-d", database, "-AtF", "|", "-c", sql);
        return result.Ok
            ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('|')).ToList()
            : null;
    }

    /// <summary>
    /// The time of the database in seconds since 1970, not of the laptop: the payments get theirs in the pods, and
    /// Prometheus stamps its samples, on the clock of the same WSL machine, and the clock of WSL can drift from Windows.
    /// </summary>
    public static async Task<double> ClockAsync() =>
        (await QueryAsync("payments", "select extract(epoch from now())"))?.FirstOrDefault()?[0] is { } text
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : throw new DrillException("Cannot read the time of PostgreSQL: is the cluster pg up?");

    /// <summary>A time of <see cref="ClockAsync"/> in SQL: to_timestamp(1795308369.123456).</summary>
    public static string Time(double seconds) => $"to_timestamp({seconds.ToString("0.######", CultureInfo.InvariantCulture)})";

    /// <summary>A number from the first column of the first row; null if the query failed.</summary>
    public static async Task<long?> NumberAsync(string database, string sql) =>
        await QueryAsync(database, sql) is [[var value, ..], ..] && long.TryParse(value, out var number) ? number : null;
}
