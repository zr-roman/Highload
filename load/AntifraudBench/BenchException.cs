namespace AntifraudBench;

/// <summary>The bench cannot go on: a missing tool, a container that does not start. The message says what to do.</summary>
public sealed class BenchException(string message) : Exception(message);
