namespace DevCli.Commands;

/// <summary>Release-pipeline stage graph (port of publish-release.ps1 stages).</summary>
public static class ReleaseStages
{
    public static readonly string[] All =
    [
        "0. Preflight",
        "1. Unit tests",
        "2. Packages",
        "3. Screenshots",
        "4. WACK",
        "5. Submission",
        "6. Manifest",
        "7. Review",
    ];

    /// <summary>Empty filter = all stages. Entries match by index ("2") or name fragment ("wack").</summary>
    public static List<int> Select(string[] filter)
    {
        if (filter.Length == 0)
        {
            return Enumerable.Range(0, All.Length).ToList();
        }

        var selected = new List<int>();
        foreach (string f in filter)
        {
            string t = f.Trim().ToLowerInvariant();
            bool matched = false;
            for (int i = 0; i < All.Length; i++)
            {
                if (t == i.ToString() || All[i].ToLowerInvariant().Contains(t))
                {
                    if (!selected.Contains(i))
                    {
                        selected.Add(i);
                    }

                    matched = true;
                }
            }

            if (!matched)
            {
                throw new Core.DecoException(
                    $"Unknown stage '{f}'. Use --list-stages (0-7 or names).",
                    "release-bad-stage",
                    Core.ExitCodes.UsageOrAuthNeeded);
            }
        }

        selected.Sort();
        return selected;
    }
}
