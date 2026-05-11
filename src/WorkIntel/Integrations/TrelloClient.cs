using System;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace WorkIntel.Integrations;

/// <summary>
/// Minimal Trello v1 API wrapper. Trello threads auth through the query string
/// (key + token), which is the one detail this client carries that the others
/// don't — the base stays auth-mechanism-agnostic and we build the QS here.
/// Docs: https://developer.atlassian.com/cloud/trello/rest/api-group-cards
/// </summary>
public sealed class TrelloClient : BaseHttpClient
{
    private readonly TrelloSecrets _secrets;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_secrets.ApiKey) &&
        !string.IsNullOrWhiteSpace(_secrets.Token);

    public string? DefaultListId => _secrets.DefaultListId;

    public TrelloClient(TrelloSecrets secrets, HttpMessageHandler? handler = null)
        : base("trello", "https://api.trello.com/1/", timeout: TimeSpan.FromSeconds(20), handler: handler)
    {
        _secrets = secrets;
    }

    public async Task<CardResponse?> CreateCardAsync(string idList, string name, string? description, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Trello is not configured.");

        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["key"] = _secrets.ApiKey;
        qs["token"] = _secrets.Token;
        qs["idList"] = idList;
        qs["name"] = name;
        if (!string.IsNullOrWhiteSpace(description)) qs["desc"] = description;

        var req = new HttpRequestMessage(HttpMethod.Post, $"cards?{qs}");
        return await SendAsync<CardResponse>(req, "POST /cards", ct).ConfigureAwait(false);
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("API key and token are required.");
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["key"] = _secrets.ApiKey;
        qs["token"] = _secrets.Token;

        var me = await GetJsonAsync<MemberMeResponse>($"members/me?{qs}", "GET /members/me", ct).ConfigureAwait(false);
        var name = me?.FullName ?? me?.Username ?? "unknown";
        return $"connected as {name}";
    }

    public sealed class CardResponse
    {
        [JsonPropertyName("id")]       public string? Id { get; set; }
        [JsonPropertyName("name")]     public string? Name { get; set; }
        [JsonPropertyName("url")]      public string? Url { get; set; }
        [JsonPropertyName("shortUrl")] public string? ShortUrl { get; set; }
    }

    public sealed class MemberMeResponse
    {
        [JsonPropertyName("fullName")] public string? FullName { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
    }
}
