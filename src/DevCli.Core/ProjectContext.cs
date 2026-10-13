namespace DevCli.Core;

/// <summary>Resolved view of the target repo. Every future command receives this, never raw paths.</summary>
public sealed record ProjectContext(
    string RepoRoot,
    string AppId,
    string? CsprojPath,
    string? Version,
    string? InformationalVersion,
    string? StoreAppId,
    bool IsManifestFound,
    string[] Locales,
    string MsixDir,
    string StoreDir,
    string ListingSourceDir,
    string? VmName,
    string? VmUser);
