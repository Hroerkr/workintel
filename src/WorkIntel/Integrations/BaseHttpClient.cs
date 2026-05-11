using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.App;

namespace WorkIntel.Integrations;

/// <summary>
/// Common HTTP plumbing for the integration clients (Harvest / Trello / Slack).
/// Owns an <see cref="HttpClient"/>, sets a service-specific user agent, and
/// provides a small set of helpers that handle the "send, log non-2xx with a
/// truncated body, throw <see cref="HttpRequestException"/>" pattern each
/// client was previously open-coding.
/// </summary>
/// <remarks>
/// <para>
/// Auth strategy is deliberately left to subclasses — Harvest and Slack put
/// credentials in headers, Trello threads them through the query string. The
/// base stays auth-mechanism-agnostic.
/// </para>
/// <para>
/// Slack's "200 OK with <c>ok: false</c>" pattern is also a subclass concern;
/// the base reports HTTP-level success only.
/// </para>
/// </remarks>
public abstract class BaseHttpClient : IDisposable
{
    protected HttpClient Http { get; }
    protected string ServiceName { get; }

    private readonly bool _disposeHandler;

    protected BaseHttpClient(
        string serviceName,
        string baseUrl,
        TimeSpan? timeout = null,
        HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) throw new ArgumentException("required", nameof(serviceName));
        if (string.IsNullOrWhiteSpace(baseUrl))     throw new ArgumentException("required", nameof(baseUrl));

        ServiceName = serviceName;

        // If the caller supplied a handler (typically for tests), don't take ownership of it.
        // If we created the default handler implicitly, HttpClient.Dispose handles cleanup.
        _disposeHandler = handler is null;
        Http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);

        Http.BaseAddress = new Uri(baseUrl);
        Http.Timeout = timeout ?? TimeSpan.FromSeconds(20);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("WorkIntel/0.1");
    }

    /// <summary>Send a fully-built request, log + throw on non-2xx, otherwise return the response
    /// for the caller to read (caller owns disposal).</summary>
    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string label, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warn($"{ServiceName} {label} → transport failure: {ex.Message}");
            throw;
        }

        if (resp.IsSuccessStatusCode) return resp;

        string body = "";
        try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { /* best-effort */ }
        int code = (int)resp.StatusCode;
        string reason = resp.ReasonPhrase ?? "";
        Log.Warn($"{ServiceName} {label} → {code} {reason}; body={Truncate(body, 400)}");
        resp.Dispose();
        throw new HttpRequestException($"{ServiceName} {label} failed: {code} {reason}");
    }

    /// <summary>Send and parse the response body as JSON.</summary>
    protected async Task<T?> SendAsync<T>(HttpRequestMessage request, string label, CancellationToken ct)
    {
        using var resp = await SendAsync(request, label, ct).ConfigureAwait(false);
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Convenience: GET a JSON resource.</summary>
    protected Task<T?> GetJsonAsync<T>(string path, string label, CancellationToken ct)
        => SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, path), label, ct);

    /// <summary>Convenience: POST a JSON body and parse the response as JSON.</summary>
    protected Task<TResponse?> PostJsonAsync<TResponse>(string path, object body, string label, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        return SendAsync<TResponse>(req, label, ct);
    }

    /// <summary>Truncate noisy error bodies before logging — server stack traces can run thousands of lines.</summary>
    protected internal static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    public virtual void Dispose() => Http.Dispose();
}
