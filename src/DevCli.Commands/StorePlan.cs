using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DevCli.Commands;

/// <summary>
/// Port of Scripts/New-StoreSubmissionPlan.ps1: offline submission contract
/// (payload + upload ZIP + plan JSON). No Partner Center calls — like the
/// ps1, refuses to overwrite existing plans and records hashes of everything.
/// </summary>
public static class StorePlan
{
    public sealed record BuiltPlan(string PlanPath, string ZipPath, string PayloadSha256);

    public static BuiltPlan Build(
        string submissionSnapshotPath,
        string bundlePath,
        string candidateDir,
        string version,
        StoreListingBundle.Bundle bundle,
        StoreShots.TestedManifest testedShots,
        string[] locales)
    {
        if (!File.Exists(submissionSnapshotPath))
        {
            throw new Core.DecoException($"Submission snapshot not found: {submissionSnapshotPath}", "store-snapshot-missing");
        }

        if (!File.Exists(bundlePath))
        {
            throw new Core.DecoException($"Submission bundle is missing: {bundlePath}", "store-bundle-missing");
        }

        JsonElement submission;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(submissionSnapshotPath, Encoding.UTF8));
            submission = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            throw new Core.DecoException($"Partner Center submission snapshot is not valid JSON: {ex.Message}", "store-snapshot-json");
        }

        string reviewDir = Path.Combine(candidateDir, "review");
        if (!Directory.Exists(reviewDir))
        {
            throw new Core.DecoException($"Candidate review directory is missing: {reviewDir}", "store-review-missing");
        }

        string planPath = Path.Combine(reviewDir, "submission-payload.json");
        string zipPath = Path.Combine(reviewDir, "submission-upload.zip");
        if (File.Exists(planPath))
        {
            throw new Core.DecoException($"Refusing to overwrite existing submission plan: {planPath}", "store-plan-exists");
        }

        if (File.Exists(zipPath))
        {
            throw new Core.DecoException($"Refusing to overwrite existing submission ZIP: {zipPath}", "store-plan-exists");
        }

        StoreSubmissionContract.PayloadPlan payload = StoreSubmissionContract.NewPayload(
            submission, bundle, testedShots.Manifest, candidateDir,
            Path.GetFileName(bundlePath), locales);
        StoreSubmissionContract.NewUploadZip(payload, bundlePath, zipPath);

        string payloadJson = JsonSerializer.Serialize(payload.Payload, new JsonSerializerOptions { WriteIndented = false });
        string payloadSha = StoreJson.Sha256Text(payloadJson);

        var assetRows = new List<string>
        {
            StoreJson.CanonicalWriter.Write(w =>
            {
                w.BeginObject();
                w.PropertyName("kind"); w.Value("package");
                w.PropertyName("fileName"); w.Value(Path.GetFileName(bundlePath));
                w.PropertyName("sha256"); w.Value(StoreJson.Sha256File(bundlePath));
                w.EndObject();
            }),
        };
        assetRows.AddRange(payload.Images.Select(image => StoreJson.CanonicalWriter.Write(w =>
        {
            w.BeginObject();
            w.PropertyName("kind"); w.Value("screenshot");
            w.PropertyName("locale"); w.Value(image.Locale);
            w.PropertyName("id"); w.Value(image.Id);
            w.PropertyName("fileName"); w.Value(image.FileName);
            w.PropertyName("sourcePath"); w.Value(image.RelativePath);
            w.PropertyName("sha256"); w.Value(image.Sha256);
            w.PropertyName("caption"); w.Value(image.Caption);
            w.EndObject();
        })));

        string plan = StoreJson.CanonicalWriter.Write(w =>
        {
            w.BeginObject();
            w.PropertyName("schemaVersion"); w.Value((long)1);
            w.PropertyName("mode"); w.Value("offline-only");
            w.PropertyName("version"); w.Value(version);
            w.PropertyName("createdUtc"); w.Value(DateTime.UtcNow.ToString("o"));
            w.PropertyName("sourceSnapshotSha256"); w.Value(StoreJson.Sha256File(submissionSnapshotPath));
            w.PropertyName("screenshotManifestSha256"); w.Value(testedShots.ManifestSha);
            w.PropertyName("payloadSha256"); w.Value(payloadSha);
            w.PropertyName("locales");
            w.BeginArray();
            foreach (string l in payload.Locales)
            {
                w.Value(l);
            }

            w.EndArray();
            w.PropertyName("payload");
            using var payloadDoc = JsonDocument.Parse(payloadJson);
            StoreJson.WriteElement(w, payloadDoc.RootElement);
            w.PropertyName("assets");
            w.BeginArray();
            foreach (string row in assetRows)
            {
                w.Raw(row);
            }

            w.EndArray();
            w.PropertyName("uploadArchive");
            w.BeginObject();
            w.PropertyName("fileName"); w.Value(Path.GetFileName(zipPath));
            w.PropertyName("sha256"); w.Value(StoreJson.Sha256File(zipPath));
            w.EndObject();
            w.EndObject();
        });

        File.WriteAllText(planPath, Pretty(plan), new UTF8Encoding(false));
        return new BuiltPlan(planPath, zipPath, payloadSha);
    }

    private static string Pretty(string compact)
    {
        using var doc = JsonDocument.Parse(compact);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
}
