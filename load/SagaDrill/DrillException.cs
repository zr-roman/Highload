namespace SagaDrill;

/// <summary>The stand is not as the drill needs it, or a step of the drill failed: the message says which.</summary>
public sealed class DrillException(string message) : Exception(message);
