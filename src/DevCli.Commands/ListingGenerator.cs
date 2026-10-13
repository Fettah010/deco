namespace DevCli.Commands;

/// <summary>
/// Pure port of Scripts/new-store-listing.ps1: drafts Partner Center listing
/// files from README.md (title + 3-line blurb + bullets) plus the two static
/// checklists. String-in/string-out; the handler owns all filesystem writes.
/// </summary>
public static class ListingGenerator
{
    public sealed record ReadmeFacts(string Title, List<string> Blurb, List<string> Bullets);

    public static ReadmeFacts ParseReadme(string readmeText)
    {
        string title = string.Empty;
        var blurb = new List<string>();
        var bullets = new List<string>();
        bool inFence = false;

        foreach (string raw in readmeText.Replace("\r\n", "\n").Split('\n'))
        {
            string t = raw.Trim();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(title) && t.StartsWith("# ", StringComparison.Ordinal))
            {
                title = t[2..].Trim();
                continue;
            }

            if (blurb.Count < 3 && t.Length > 0 && !t.StartsWith('#') && !t.StartsWith('<')
                && !t.StartsWith('!') && !t.StartsWith('[') && !t.StartsWith('`'))
            {
                blurb.Add(t);
                continue;
            }

            if (t.StartsWith("- ", StringComparison.Ordinal))
            {
                bullets.Add(t[2..].Trim());
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = "My App";
        }

        return new ReadmeFacts(title, blurb, bullets);
    }

    public static string BuildDescription(ReadmeFacts facts)
    {
        var lines = new List<string> { facts.Title, string.Empty };
        foreach (string b in facts.Blurb)
        {
            lines.Add(b);
            lines.Add(string.Empty);
        }

        if (facts.Bullets.Count > 0)
        {
            lines.Add("Features:");
            lines.AddRange(facts.Bullets.Select(b => "- " + b));
            lines.Add(string.Empty);
        }

        lines.Add("(Draft generated from README.md - edit before pasting into Partner Center. Limit: 10000 characters.)");
        return string.Join("\r\n", lines);
    }

    public const string ScreenshotsChecklist =
        "# Screenshots checklist\r\n" +
        "\r\n" +
        "- At least 1 screenshot (4+ recommended) per device family you target.\r\n" +
        "- Minimum 1366x768; 1920x1080 or 2560x1440 preferred. PNG or JPG.\r\n" +
        "- Show the real app (Home, Settings, Update Center) - no mockups, no\r\n" +
        "  letterboxing, no marketing overlays covering UI.\r\n" +
        "- Capture light AND dark theme once each if you support both.\r\n" +
        "- File names: screenshot-01-home.png, screenshot-02-settings.png, ...";

    public const string SubmissionChecklist =
        "# Submission checklist (Partner Center > app > new submission)\r\n" +
        "\r\n" +
        "- [ ] Packages: upload the .msixupload from Releases/Msix.\r\n" +
        "- [ ] Pricing and availability: price, markets, audience.\r\n" +
        "- [ ] Properties: category, capabilities (justify each), age rating\r\n" +
        "  questionnaire (answer for the actual content - updates change it).\r\n" +
        "- [ ] Store listings: paste description.txt, upload screenshots.\r\n" +
        "- [ ] Privacy policy URL (required for most categories).\r\n" +
        "- [ ] Submit; certification takes up to ~3 business days.";
}
