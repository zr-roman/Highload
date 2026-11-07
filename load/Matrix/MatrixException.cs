namespace Matrix;

/// <summary>A problem the user can fix: the matrix prints the message and stops, without a stack trace.</summary>
public sealed class MatrixException(string message) : Exception(message);
