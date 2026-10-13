namespace DevCli.Commands;

/// <summary>
/// Capture-lab identity resolution: an explicit flag wins, manifest
/// <c>capture.vmName/vmUser</c> comes second, legacy built-ins last (compat
/// for repos without a manifest). <see cref="UsedLegacyFallback"/> lets
/// <c>doctor</c> report unpinned repos instead of surprising them.
/// </summary>
public static class VmConfig
{
    public const string LegacyVmName = "Formixa-Capture";
    public const string LegacyVmUser = "cap";

    public static string ResolveName(string? flag, string? manifest) =>
        FirstNonBlank(flag, manifest) ?? LegacyVmName;

    public static string ResolveUser(string? flag, string? manifest) =>
        FirstNonBlank(flag, manifest) ?? LegacyVmUser;

    public static bool UsedLegacyFallback(string? flag, string? manifest) =>
        FirstNonBlank(flag, manifest) is null;

    private static string? FirstNonBlank(string? flag, string? manifest) =>
        !string.IsNullOrWhiteSpace(flag) ? flag!.Trim()
        : !string.IsNullOrWhiteSpace(manifest) ? manifest!.Trim()
        : null;
}
