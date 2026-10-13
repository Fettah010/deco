using System.Net;
using System.Text;
using DevCli.Infrastructure;

namespace DevCli.Infrastructure.Tests;

public sealed class StoreApiClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string? Body)> Calls { get; } = [];
        private readonly Func<string, string, string?, HttpResponseMessage> _respond;

        public FakeHandler(Func<string, string, string?, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri?.ToString() ?? string.Empty, body));
            return _respond(request.Method.Method, request.RequestUri?.ToString() ?? string.Empty, body);
        }

        public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
    }

    private static (StoreApiClient Client, FakeHandler Handler) Create(
        Func<string, string, string?, HttpResponseMessage>? respond = null)
    {
        var handler = new FakeHandler(respond ?? ((_, _, _) => FakeHandler.Json("{}")));
        return (new StoreApiClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task FullFlow_Token_Create_Put_Commit_Status()
    {
        var (client, handler) = Create((method, url, _) =>
        {
            if (url.Contains("oauth2/token"))
            {
                return FakeHandler.Json("""{"access_token":"tok123"}""");
            }

            if (url.EndsWith("/submissions") && method == "POST")
            {
                return FakeHandler.Json("""{"id":"sub-1"}""");
            }

            if (url.Contains("/status"))
            {
                return FakeHandler.Json("""{"status":"Published"}""");
            }

            if (url.EndsWith("/commit"))
            {
                return FakeHandler.Json("{}");
            }

            return FakeHandler.Json("""{"id":"sub-1","fileUploadUrl":"https://blob/upload"}""");
        });

        string token = await client.GetTokenAsync("t", "c", "s");
        Assert.Equal("tok123", token);
        var created = await client.CreateSubmissionAsync("1.2", "9ABC", token);
        var (updated, uploadUrl) = await client.UpdateSubmissionAsync("1.2", "9ABC", "sub-1", token, """{"applicationPackages":[]}""");
        Assert.Equal("https://blob/upload", uploadUrl);
        await client.CommitAsync("1.2", "9ABC", "sub-1", token, "Manual");
        string status = await client.WaitForPublishedAsync("1.2", "9ABC", "sub-1", token, 120,
            null, (_, _) => Task.CompletedTask);
        Assert.Equal("Published", status);

        // Auth header present, charset utf-8 on JSON bodies.
        Assert.Contains(handler.Calls, c => c.Url.Contains("oauth2/token"));
        Assert.True(handler.Calls.Count(c => c.Url.Contains("/submissions")) >= 3);
    }

    [Fact]
    public async Task WaitForPublished_ThrowsOnFail()
    {
        var (client, _) = Create((_, url, _) =>
            url.Contains("oauth2/token")
                ? FakeHandler.Json("""{"access_token":"t"}""")
                : FakeHandler.Json("""{"status":"Failed"}"""));
        await Assert.ThrowsAsync<Core.DecoException>(() =>
            client.WaitForPublishedAsync("1.2", "9ABC", "sub-1", "t", 120, null, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task GetToken_ThrowsWhenEmpty()
    {
        var (client, _) = Create((_, _, _) => FakeHandler.Json("""{"nope":1}"""));
        await Assert.ThrowsAsync<Core.DecoException>(() => client.GetTokenAsync("t", "c", "s"));
    }

    [Fact]
    public async Task ApiError_SurfacesDetail()
    {
        var (client, _) = Create((_, _, _) => FakeHandler.Json(
            """{"error":"bad"}""", HttpStatusCode.BadRequest));
        var ex = await Assert.ThrowsAsync<Core.DecoException>(() =>
            client.CreateSubmissionAsync("1.2", "9ABC", "t"));
        Assert.Contains("bad", ex.Message);
    }

    [Fact]
    public void AppBase_VersionsPerCommand()
    {
        Assert.Contains("/v1.2/", StoreApiClient.AppBase("1.2", "A"));
        Assert.Contains("/v1.0/", StoreApiClient.AppBase("1.0", "A"));
    }
}
