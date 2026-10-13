using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DevCli.Commands;

/// <summary>
/// API-path submission builders (port of the Build-SubmissionListings /
/// Build-SubmissionZip helpers): patch description/notes/images/packages on
/// a full GET submission object (full-object PUT, never partial) and build
/// the single upload ZIP (bundle + loose PNGs at the root).
/// </summary>
public static class StoreApiSubmission
{
    public static string BuildSubmissionListings(
        string submissionJson, string descriptionFile, string releaseNotes,
        string? imagesDir, string bundleFileName)
    {
        if (!File.Exists(descriptionFile))
        {
            throw new Core.DecoException($"Description file not found: {descriptionFile}.", "store-description-missing");
        }

        string description = File.ReadAllText(descriptionFile, Encoding.UTF8).Trim();
        if (description.Length > 10000)
        {
            throw new Core.DecoException("Description exceeds the 10000-character Store limit.", "store-description-long");
        }

        var images = new List<(string FileName, string Description)>();
        if (!string.IsNullOrWhiteSpace(imagesDir) && Directory.Exists(imagesDir))
        {
            foreach (string shot in Directory.GetFiles(imagesDir, "*.png").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (new FileInfo(shot).Length == 0)
                {
                    throw new Core.DecoException($"Empty screenshot: {shot}.", "store-shot-empty");
                }

                images.Add((Path.GetFileName(shot), Path.GetFileNameWithoutExtension(shot)));
            }
        }

        using var doc = JsonDocument.Parse(submissionJson);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("listings", out JsonElement listings)
            || !listings.TryGetProperty("en-us", out _))
        {
            throw new Core.DecoException("Submission has no en-us listing to patch.", "store-listing-missing");
        }

        return StoreJson.CanonicalWriter.Write(w =>
        {
            w.BeginObject();
            foreach (JsonProperty p in root.EnumerateObject())
            {
                if (p.NameEquals("listings"))
                {
                    w.PropertyName("listings");
                    w.BeginObject();
                    foreach (JsonProperty loc in p.Value.EnumerateObject())
                    {
                        w.PropertyName(loc.Name);
                        if (loc.Value.ValueKind == JsonValueKind.Object
                            && loc.Value.TryGetProperty("baseListing", out _))
                        {
                            w.BeginObject();
                            foreach (JsonProperty lp in loc.Value.EnumerateObject())
                            {
                                if (lp.NameEquals("baseListing"))
                                {
                                    w.PropertyName("baseListing");
                                    w.BeginObject();
                                    foreach (JsonProperty bp in lp.Value.EnumerateObject())
                                    {
                                        string n = bp.Name;
                                        if (n == "description")
                                        {
                                            w.PropertyName(n); w.Value(description);
                                        }
                                        else if (n == "releaseNotes")
                                        {
                                            w.PropertyName(n); w.Value(releaseNotes);
                                        }
                                        else if (n == "images" && images.Count > 0)
                                        {
                                            w.PropertyName(n);
                                            w.BeginArray();
                                            foreach ((string fileName, string desc) in images)
                                            {
                                                w.BeginObject();
                                                w.PropertyName("fileName"); w.Value(fileName);
                                                w.PropertyName("fileStatus"); w.Value("PendingUpload");
                                                w.PropertyName("description"); w.Value(desc);
                                                w.PropertyName("imageType"); w.Value("Screenshot");
                                                w.EndObject();
                                            }

                                            w.EndArray();
                                        }
                                        else
                                        {
                                            w.PropertyName(n);
                                            StoreJson.WriteElement(w, bp.Value);
                                        }
                                    }

                                    w.EndObject();
                                }
                                else
                                {
                                    w.PropertyName(lp.Name);
                                    StoreJson.WriteElement(w, lp.Value);
                                }
                            }

                            w.EndObject();
                        }
                        else
                        {
                            w.PropertyName(loc.Name);
                            StoreJson.WriteElement(w, loc.Value);
                        }
                    }

                    w.EndObject();
                }
                else if (p.NameEquals("applicationPackages"))
                {
                    w.PropertyName("applicationPackages");
                    w.BeginArray();
                    w.BeginObject();
                    w.PropertyName("fileName"); w.Value(bundleFileName);
                    w.PropertyName("fileStatus"); w.Value("PendingUpload");
                    w.PropertyName("minimumDirectXVersion"); w.Value("None");
                    w.PropertyName("minimumSystemRam"); w.Value("None");
                    w.EndObject();
                    w.EndArray();
                }
                else
                {
                    w.PropertyName(p.Name);
                    StoreJson.WriteElement(w, p.Value);
                }
            }

            w.EndObject();
        });
    }

    public static string BuildSubmissionZip(string bundlePath, string? imagesDir, string zipPath)
    {
        if (!File.Exists(bundlePath))
        {
            throw new Core.DecoException($"Bundle not found: {bundlePath}.", "store-bundle-missing");
        }

        string stage = Path.Combine(Path.GetTempPath(), "store-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            File.Copy(bundlePath, Path.Combine(stage, Path.GetFileName(bundlePath)), overwrite: true);
            if (!string.IsNullOrWhiteSpace(imagesDir) && Directory.Exists(imagesDir))
            {
                foreach (string shot in Directory.GetFiles(imagesDir, "*.png").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    File.Copy(shot, Path.Combine(stage, Path.GetFileName(shot)), overwrite: true);
                }
            }

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(stage, zipPath);
        }
        finally
        {
            try
            {
                Directory.Delete(stage, recursive: true);
            }
            catch
            {
            }
        }

        return zipPath;
    }
}
