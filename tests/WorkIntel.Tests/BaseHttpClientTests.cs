using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.Integrations;
using Xunit;

namespace WorkIntel.Tests;

public sealed class BaseHttpClientTests
{
    /// <summary>Test handler that captures every outgoing request and replies with whatever
    /// <see cref="Respond"/> returns.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Captured { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Captured.Add(request);
            if (Respond is null) throw new InvalidOperationException("StubHandler.Respond not configured");
            return Task.FromResult(Respond(request));
        }
    }

    /// <summary>Subclass that re-exposes the protected surface for direct testing.</summary>
    private sealed class TestClient : BaseHttpClient
    {
        public TestClient(HttpMessageHandler handler)
            : base("test", "https://example.test/", timeout: TimeSpan.FromSeconds(5), handler: handler)
        { }

        public new Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, string label, CancellationToken ct = default)
            => base.SendAsync(req, label, ct);

        public new Task<T?> SendAsync<T>(HttpRequestMessage req, string label, CancellationToken ct = default)
            => base.SendAsync<T>(req, label, ct);

        public new Task<T?> GetJsonAsync<T>(string path, string label, CancellationToken ct = default)
            => base.GetJsonAsync<T>(path, label, ct);

        public new Task<T?> PostJsonAsync<T>(string path, object body, string label, CancellationToken ct = default)
            => base.PostJsonAsync<T>(path, body, label, ct);
    }

    private sealed class Greeting
    {
        [JsonPropertyName("hello")] public string? Hello { get; set; }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task SendAsync_ResolvesPathAgainstBaseAddress()
    {
        using var stub = new StubHandler { Respond = _ => Json(HttpStatusCode.OK, "{}") };
        using var c = new TestClient(stub);

        await c.SendAsync(new HttpRequestMessage(HttpMethod.Get, "things/42"), "test");

        Assert.Single(stub.Captured);
        Assert.Equal("https://example.test/things/42", stub.Captured[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task SendAsync_PassesUserAgent()
    {
        using var stub = new StubHandler { Respond = _ => Json(HttpStatusCode.OK, "{}") };
        using var c = new TestClient(stub);

        await c.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/"), "test");

        var ua = stub.Captured[0].Headers.UserAgent.ToString();
        Assert.Contains("WorkIntel", ua);
    }

    [Fact]
    public async Task GetJsonAsync_ParsesResponseBody()
    {
        using var stub = new StubHandler { Respond = _ => Json(HttpStatusCode.OK, """{"hello":"world"}""") };
        using var c = new TestClient(stub);

        var result = await c.GetJsonAsync<Greeting>("greet", "GET /greet");

        Assert.NotNull(result);
        Assert.Equal("world", result!.Hello);
    }

    [Fact]
    public async Task PostJsonAsync_SetsJsonContentType_AndSerializesBody()
    {
        using var stub = new StubHandler { Respond = _ => Json(HttpStatusCode.OK, "{}") };
        using var c = new TestClient(stub);

        await c.PostJsonAsync<Greeting>("send", new { hello = "world" }, "POST /send");

        var req = stub.Captured[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.NotNull(req.Content);
        Assert.Equal("application/json", req.Content!.Headers.ContentType?.MediaType);

        var sentBody = await req.Content.ReadAsStringAsync();
        Assert.Contains("\"hello\":", sentBody);
        Assert.Contains("\"world\"", sentBody);
    }

    [Fact]
    public async Task SendAsync_OnNon2xx_ThrowsWithStatusInMessage()
    {
        using var stub = new StubHandler { Respond = _ => Json(HttpStatusCode.BadRequest, """{"error":"bad"}""") };
        using var c = new TestClient(stub);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            c.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/"), "GET /"));

        Assert.Contains("400", ex.Message);
        Assert.Contains("test", ex.Message); // service name is in the failure label
    }

    [Fact]
    public async Task SendAsync_On500_StillThrows()
    {
        using var stub = new StubHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("oh no")
            }
        };
        using var c = new TestClient(stub);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            c.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/"), "GET /"));
    }

    [Fact]
    public async Task SendAsync_TransportFailure_PropagatesOriginalException()
    {
        using var stub = new StubHandler
        {
            Respond = _ => throw new HttpRequestException("dns blew up")
        };
        using var c = new TestClient(stub);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            c.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/"), "GET /"));

        Assert.Contains("dns blew up", ex.Message);
    }

    [Fact]
    public void Truncate_ShortInput_IsUnchanged()
    {
        Assert.Equal("hi", BaseHttpClient.Truncate("hi", 100));
    }

    [Fact]
    public void Truncate_LongInput_ClipsAndAppendsEllipsis()
    {
        var input = new string('x', 600);
        var t = BaseHttpClient.Truncate(input, 50);
        Assert.Equal(50, t.Length);
        Assert.EndsWith("…", t);
    }

    [Fact]
    public async Task Cancellation_PropagatesToHandler()
    {
        var entered = new TaskCompletionSource();
        var releaseHandler = new TaskCompletionSource();

        var slow = new SlowHandler(entered, releaseHandler);
        using var c = new TestClient(slow);

        using var cts = new CancellationTokenSource();
        var task = c.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/"), "GET /", cts.Token);

        await entered.Task;
        cts.Cancel();
        releaseHandler.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _release;

        public SlowHandler(TaskCompletionSource entered, TaskCompletionSource release)
        {
            _entered = entered;
            _release = release;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _entered.TrySetResult();
            await Task.WhenAny(_release.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
