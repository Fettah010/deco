namespace DevCli.Commands;

/// <summary>
/// Pure port of the measure-publish-weight.ps1 comparison math:
/// total publish bytes vs docs/publish-weight-baseline.json, 10% guardrail.
/// Filesystem + publish stay in the handler; this is string/number-in only.
/// </summary>
public static class WeightCheck
{
    public sealed record Evaluation(
        double Mb,
        long? BaselineBytes,
        double? BaselineMb,
        double? DeltaPct,
        bool Jumped);

    public static Evaluation Evaluate(long bytes, long? baselineBytes)
    {
        double mb = Math.Round(bytes / (1024.0 * 1024.0), 1);
        if (baselineBytes is null or <= 0)
        {
            return new Evaluation(mb, null, null, null, false);
        }

        double ratio = bytes / (double)baselineBytes.Value;
        double pct = Math.Round((ratio - 1) * 100, 1);
        return new Evaluation(
            mb,
            baselineBytes,
            Math.Round(baselineBytes.Value / (1024.0 * 1024.0), 1),
            pct,
            ratio > 1.1);
    }

    public static long SumDirectoryBytes(string dir)
    {
        long total = 0;
        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(f).Length;
            }
            catch
            {
                // Vanishing temp file: skip, like Measure-Object would on a retry.
            }
        }

        return total;
    }
}
