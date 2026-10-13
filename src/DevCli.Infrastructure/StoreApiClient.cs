using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DevCli.Infrastructure;

/// <summary>
/// Port of Scripts/store-api.ps1 + submit-store.ps1 over HttpClient:
/// Entra client-credentials token, submission CRUD (create/get/put/commit/
/// status/delete) against the Partner Center submission API, BlockBlob ZIP
/// upload, backoff status polling, listing patch + submission ZIP builders.
/// Secrets travel in memory only (never logged). HttpClient is injected so
/// tests run fully offline with a canned handler.
/// </summary>
public sealed class StoreApiClient
{
    public const string DevCenterBase = "https://manage.devcenter.microsoft.com";
    public const string LoginBase = "https://login.microsoftonline.com";

    private readonly HttpClient _http;

    public StoreApiClient(HttpClient http) => _http = http;

    public static string AppBase(string apiVersion, string appId) =>
        $"{DevCenterBase}/v{apiVersion}/my/applications/{appId}";

    public async Task<string> GetTokenAsync(string tenantId, string clientId, string clientSecret, CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["resource"] = "https://manage.devcenter.microsoft.com",
        });
        using HttpResponseMessage resp = await _http.PostAsync(
            $"{LoginBase}/{tenantId}/oauth2/token", form, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new Core.DecoException(
                $"Store authentication failed (HTTP {(int)resp.StatusCode}). Check tenant/client/secret expiry.",
                "store-auth-failed");
        }

        string? token = JsonProperty(body, "access_token");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new Core.DecoException("Authentication returned no token.", "store-auth-failed");
        }

        return token;
    }

    public async Task<JsonElement> CreateSubmissionAsync(string apiVersion, string appId, string token, CancellationToken ct = default)
    {
        JsonElement submission = await SendAsync(HttpMethod.Post,
            $"{AppBase(apiVersion, appId)}/submissions", token, null, ct);
        if (string.IsNullOrWhiteSpace(Str(submission, "id")))
        {
            throw new Core.DecoException("Submission creation returned no id.", "store-submit-failed");
        }

        return submission;
    }

    public Task<JsonElement> GetSubmissionAsync(string apiVersion, string appId, string submissionId, string token, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"{AppBase(apiVersion, appId)}/submissions/{submissionId}", token, null, ct);

    /// <summary>Full-object PUT (never partial — partial bodies risk nulling pricing/listings).</summary>
    public async Task<(JsonElement Updated, string UploadUrl)> UpdateSubmissionAsync(
        string apiVersion, string appId, string submissionId, string token, string jsonBody, CancellationToken ct = default)
    {
        JsonElement updated = await SendAsync(HttpMethod.Put,
            $"{AppBase(apiVersion, appId)}/submissions/{submissionId}", token, jsonBody, ct);
        string uploadUrl = Str(updated, "fileUploadUrl");
        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            throw new Core.DecoException("Submission update returned no fileUploadUrl.", "store-submit-failed");
        }

        return (updated, uploadUrl);
    }

    public async Task UploadBlobAsync(string uploadUrl, string filePath, CancellationToken ct = default)
    {
        using var content = new StreamContent(File.OpenRead(filePath));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
        req.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        req.Content = content;
        using HttpResponseMessage resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new Core.DecoException(
                $"Package upload failed (HTTP {(int)resp.StatusCode}).",
                "store-upload-failed");
        }
    }

    public async Task CommitAsync(string apiVersion, string appId, string submissionId, string token, string publishMode, CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Post,
            $"{AppBase(apiVersion, appId)}/submissions/{submissionId}/commit",
            token, $"{{\"targetPublishMode\":\"{publishMode}\"}}", ct);
    }

    public async Task<string> GetStatusAsync(string apiVersion, string appId, string submissionId, string token, CancellationToken ct = default)
    {
        JsonElement status = await SendAsync(HttpMethod.Get,
            $"{AppBase(apiVersion, appId)}/submissions/{submissionId}/status", token, null, ct);
        return Str(status, "status");
    }

    public async Task DeleteSubmissionAsync(string apiVersion, string appId, string submissionId, string token, CancellationToken ct = default)
    {
        await SendAsync(HttpMethod.Delete,
            $"{AppBase(apiVersion, appId)}/submissions/{submissionId}", token, null, ct);
    }

    /// <summary>Backoff poll (30s initial, x1.5, 300s cap). Throws on *Fail* or timeout.</summary>
    public async Task<string> WaitForPublishedAsync(
        string apiVersion, string appId, string submissionId, string token,
        int timeoutSec, Action<string>? log = null,
        Func<int, CancellationToken, Task>? delay = null, CancellationToken ct = default)
    {
        delay ??= Task.Delay;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSec);
        int waitSec = 30;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await delay(waitSec * 1000, ct);
            string status = await GetStatusAsync(apiVersion, appId, submissionId, token, ct);
            log?.Invoke($"Status: {status} (polled every {waitSec}s)");
            if (string.Equals(status, "Published", StringComparison.OrdinalIgnoreCase))
            {
                return status;
            }

            if (status.Contains("Fail", StringComparison.OrdinalIgnoreCase))
            {
                throw new Core.DecoException($"Submission failed with status {status}.", "store-submit-failed");
            }

            waitSec = Math.Min(300, (int)Math.Round(waitSec * 1.5));
        }

        throw new Core.DecoException(
            $"Status poll timed out after {timeoutSec}s; check Partner Center (submission {submissionId}).",
            "store-poll-timeout");
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string url, string token, string? jsonBody, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (jsonBody is not null)
        {
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Content.Headers.ContentType.CharSet = "utf-8"; // load-bearing: listings carry non-ASCII (fr-FR)
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            throw new Core.DecoException($"Store API {method} failed: {ex.Message}", "store-api-failed");
        }

        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                string detail = string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)resp.StatusCode}" : body;
                if (detail.Length > 1000)
                {
                    detail = detail[..1000];
                }

                throw new Core.DecoException($"Store API {method} failed: {detail}", "store-api-failed");
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                using var empty = JsonDocument.Parse("{}");
                return empty.RootElement.Clone();
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                throw new Core.DecoException($"Store API {method} returned invalid JSON: {ex.Message}", "store-api-failed");
            }
        }
    }

    /// <summary>String property or empty (never throws).</summary>
    public static string Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out JsonElement e)
        && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? string.Empty
            : string.Empty;

    internal static string? JsonProperty(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Str(doc.RootElement, name);
        }
        catch
        {
            return null;
        }
    }
}
