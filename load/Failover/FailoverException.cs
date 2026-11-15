namespace Failover;

/// <summary>A problem the user can fix: the tool prints the message and stops, without a stack trace.</summary>
public sealed class FailoverException(string message) : Exception(message);
