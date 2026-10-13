namespace DevCli.Infrastructure;

/// <summary>
/// Locates Windows SDK tools (makeappx, signtool, makepri). The SDK is never
/// on PATH: probe PATH first, then every installed Kits version newest-first.
/// Pure filesystem logic; roots are injectable for tests.
/// </summary>
public static class SdkTools
{
    public static string? Find(string exeName) =>
        Find(exeName, DefaultKitsRoots(), PathEnvDirs());

    /// <summary>Testable core: explicit kits roots + PATH dirs.</summary>
    public static string? Find(string exeName, string[] kitsRoots, string[] pathDirs)
    {
        string file = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe";
        foreach (string dir in pathDirs)
        {
            try
            {
                string candidate = Path.Combine(dir, file);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Malformed PATH segment: skip.
            }
        }

        foreach (string kits in kitsRoots)
        {
            string[] versions;
            try
            {
                if (!Directory.Exists(kits))
                {
                    continue;
                }

                versions = Directory.GetDirectories(kits);
            }
            catch
            {
                continue;
            }

            Array.Sort(versions, StringComparer.OrdinalIgnoreCase);
            Array.Reverse(versions);
            foreach (string ver in versions)
            {
                string candidate = Path.Combine(ver, "x64", file);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string[] DefaultKitsRoots()
    {
        var roots = new List<string>();
        string? pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string? pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(pf86))
        {
            roots.Add(Path.Combine(pf86, "Windows Kits", "10", "bin"));
        }

        if (!string.IsNullOrWhiteSpace(pf64))
        {
            roots.Add(Path.Combine(pf64, "Windows Kits", "10", "bin"));
        }

        return [.. roots];
    }

    private static string[] PathEnvDirs()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return [];
        }

        return pathEnv.Split(Path.PathSeparator);
    }
}
