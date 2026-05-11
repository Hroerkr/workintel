using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WorkIntel.Integrations;

/// <summary>
/// Minimal Harvest v2 API wrapper for the operations we care about: starting
/// a timer (creating a running time entry) and stopping the running one.
/// Auth: Bearer token + <c>Harvest-Account-Id</c> header.
/// Docs: https://help.getharvest.com/api-v2/timesheets-api/timesheets/time-entries/
/// </summary>
public sealed class HarvestClient : BaseHttpClient
{
    private readonly HarvestSecrets _secrets;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_secrets.AccessToken) &&
        !string.IsNullOrWhiteSpace(_secrets.AccountId);

    public long? DefaultProjectId => _secrets.DefaultProjectId;
    public long? DefaultTaskId => _secrets.DefaultTaskId;

    public HarvestClient(HarvestSecrets secrets, HttpMessageHandler? handler = null)
        : base("harvest", "https://api.harvestapp.com/v2/", timeout: TimeSpan.FromSeconds(20), handler: handler)
    {
        _secrets = secrets;

        if (!string.IsNullOrWhiteSpace(_secrets.AccessToken))
            Http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _secrets.AccessToken);
        if (!string.IsNullOrWhiteSpace(_secrets.AccountId))
            Http.DefaultRequestHeaders.Add("Harvest-Account-Id", _secrets.AccountId);
    }

    public async Task<TimeEntryResponse?> ClockInAsync(long projectId, long taskId, string? notes, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Harvest is not configured.");

        var body = new
        {
            project_id = projectId,
            task_id = taskId,
            spent_date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            notes,
        };

        return await PostJsonAsync<TimeEntryResponse>("time_entries", body, "POST /time_entries", ct).ConfigureAwait(false);
    }

    public async Task<TimeEntryResponse?> ClockOutAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Harvest is not configured.");

        var running = await GetJsonAsync<TimeEntryPage>("time_entries?is_running=true", "GET /time_entries?is_running=true", ct).ConfigureAwait(false);
        var entry = running?.TimeEntries is { Count: > 0 } list ? list[0] : null;
        if (entry is null) return null;

        var req = new HttpRequestMessage(HttpMethod.Patch, $"time_entries/{entry.Id}/stop");
        return await SendAsync<TimeEntryResponse>(req, $"PATCH /time_entries/{entry.Id}/stop", ct).ConfigureAwait(false);
    }

    public async Task<string> TestAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Account ID and access token are required.");
        var me = await GetJsonAsync<UserMeResponse>("users/me", "GET /users/me", ct).ConfigureAwait(false);
        if (me is null) return "connected";
        var name = string.Join(' ', new[] { me.FirstName, me.LastName }).Trim();
        return string.IsNullOrEmpty(name) ? "connected" : $"connected as {name}";
    }

    public sealed class TimeEntryPage
    {
        [JsonPropertyName("time_entries")] public List<TimeEntryResponse>? TimeEntries { get; set; }
    }

    public sealed class TimeEntryResponse
    {
        [JsonPropertyName("id")]    public long Id { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        [JsonPropertyName("is_running")] public bool IsRunning { get; set; }
    }

    public sealed class UserMeResponse
    {
        [JsonPropertyName("first_name")] public string? FirstName { get; set; }
        [JsonPropertyName("last_name")]  public string? LastName { get; set; }
        [JsonPropertyName("email")]      public string? Email { get; set; }
    }
}
