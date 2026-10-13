namespace DevCli.Core;

/// <summary>Process exit codes. Kept stable across all future commands.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int UsageOrAuthNeeded = 2;
}
