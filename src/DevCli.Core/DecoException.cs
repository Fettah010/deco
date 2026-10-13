namespace DevCli.Core;

/// <summary>Typed failure with a stable exit code and machine-readable reason.</summary>
public sealed class DecoException : Exception
{
    public int ExitCode { get; }
    public string Reason { get; }

    public DecoException(string message, string reason, int exitCode = ExitCodes.Error)
        : base(message)
    {
        Reason = reason;
        ExitCode = exitCode;
    }
}
