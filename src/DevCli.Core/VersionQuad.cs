namespace DevCli.Core;

/// <summary>
/// MSIX version-quad math (each part 0-65534). Ported from build-msix.ps1 Get-AppVersion.
/// Pre-release suffixes (-beta) are stripped; short versions are zero-padded to 4 parts.
/// </summary>
public static class VersionQuad
{
    public static string ToMsixQuad(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new DecoException("Version is empty; cannot build an MSIX quad.", "empty-version");
        }

        string core = version.Split('-', 2)[0].Trim().TrimStart('v', 'V');
        string[] parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var padded = parts.ToList();
        while (padded.Count < 4)
        {
            padded.Add("0");
        }

        if (padded.Count > 4)
        {
            throw new DecoException($"Version '{version}' has more than 4 numeric parts.", "version-too-long");
        }

        foreach (string p in padded)
        {
            if (!int.TryParse(p, out int n) || n < 0 || n > 65534)
            {
                throw new DecoException(
                    $"Version part '{p}' in '{version}' is not valid for MSIX (0-65534 per quad).",
                    "version-part-out-of-range");
            }
        }

        return string.Join(".", padded.Take(4));
    }

    public static bool TryToMsixQuad(string? version, out string quad)
    {
        quad = string.Empty;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        try
        {
            quad = ToMsixQuad(version);
            return true;
        }
        catch (DecoException)
        {
            return false;
        }
    }
}
