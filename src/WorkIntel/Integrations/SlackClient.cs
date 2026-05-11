using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WorkIntel.App;

namespace WorkIntel.Integrations;

/// <summary>
/// Minimal Slack Web API wrapper. We only need <c>chat.postMessage</c> + <c>auth.test</c>.
/// Auth: bot user OAuth token (<c>xoxb-...</c>) in the Authorization header.
/// </summary>
/// <remarks>
/// Slack's quirk: success responses are HTTP 200 even for logical errors
/// (<c>{"ok": false, "error": "..."}</c>). We let the base treat the 200 as
/// HTTP-success and inspect <c>ok</c> at this layer.
/// </remarks>
public sealed class SlackClient : BaseHttpClient
{
    private readonly SlackSecrets _secrets;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_secrets.BotToken);
    public string? DefaultChannel => _secrets.DefaultChannel;

    public SlackClient(SlackSecrets secrets, HttpMessageHandler? handler = null)
        : base("slack", "https://slack.com/api/", timeout: TimeSpan.FromSeconds(15), handler: handler)
    {
        _secrets = secrets;
        if (!string.IsNullOrWhiteSpace(_secrets.BotToken))
            Http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _secrets.BotToken);
    }

    public async Task<PostMessageResponse?> PostMessageAsync(string channel, string text, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Slack is not configured.");

        var parsed = await PostJsonAsync<PostMessageResponse>(
            "chat.postMessage",
            new { channel, text },
            "POST chat.postMessage",
            ct).ConfigureAwait(false);

        if (parsed is null || !parsed.Ok)
        {
            Log.Warn($"slack chat.postMessage failed: ok={parsed?.Ok}, error={parsed?.Error}");
            throw new HttpRequestException($"Slack chat.postMessage failed: {parsed?.Error ?? "unknown"}");
        }
        return parsed;
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Bot token is required.");

        var req = new HttpRequestMessage(HttpMethod.Post, "auth.test");
        var parsed = await SendAsync<AuthTestResponse>(req, "POST auth.test", ct).ConfigureAwait(false);
        if (parsed is null || !parsed.Ok)
            throw new HttpRequestException($"auth.test failed: {parsed?.Error ?? "unknown"}");
        return $"connected as {parsed.User ?? "?"} in {parsed.Team ?? "?"}";
    }

    public sealed class PostMessageResponse
    {
        [JsonPropertyName("ok")]      public bool Ok { get; set; }
        [JsonPropertyName("error")]   public string? Error { get; set; }
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("ts")]      public string? Ts { get; set; }
    }

    public sealed class AuthTestResponse
    {
        [JsonPropertyName("ok")]    public bool Ok { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("user")]  public string? User { get; set; }
        [JsonPropertyName("team")]  public string? Team { get; set; }
    }
}
