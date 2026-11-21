namespace AntifraudBench;

/// <summary>One step: a number of processes, a rate, a repeat, and what k6, docker stats and Redis showed for it.</summary>
public sealed record StepResult(
    int Workers, int Rate, int Repeat, K6Result K6, CpuUsage AntifraudCpu, CpuUsage RedisCpu, CpuUsage K6Cpu, RedisUsage Redis)
{
    /// <summary>The criterion of stage 4 for this step: p99 within 20 ms, and the planned load really sent and answered.</summary>
    public bool MeetsCriterion => K6.P99 <= 20 && K6.FailedShare < 0.01 && K6.DroppedPerSecond < Rate / 100.0;

    public string ConsoleLine =>
        $"  {Workers} proc, {Rate}/s, #{Repeat}: {K6.RequestsPerSecond:0}/s answered, p50 {K6.P50:0.0} ms, p99 {K6.P99:0.0} ms, " +
        $"decided {K6.DecisionsPerSecond:0}/s, failed {K6.FailedShare:P1} (503 limit: {K6.Overloaded}, redis: {K6.RedisTimeouts}), " +
        $"dropped {K6.DroppedPerSecond:0.0}/s, " +
        $"CPU antifraud {AntifraudCpu.Median:0}% redis {RedisCpu.Median:0}% k6 {K6Cpu.Median:0}%";
}
