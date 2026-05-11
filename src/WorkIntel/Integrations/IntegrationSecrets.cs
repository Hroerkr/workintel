namespace WorkIntel.Integrations;

/// <summary>
/// Plain-old config record loaded from <c>%LOCALAPPDATA%\WorkIntel\config.json</c>
/// (or env vars) by <see cref="SecretsLoader"/>. Phase 4 will move this behind
/// DPAPI and a settings UI; for now it's user-readable JSON gated by NTFS ACLs.
/// </summary>
public sealed class IntegrationSecrets
{
    public HarvestSecrets? Harvest { get; set; }
    public TrelloSecrets? Trello { get; set; }
    public SlackSecrets? Slack { get; set; }
}

public sealed class HarvestSecrets
{
    /// <summary>Harvest account ID (numeric, sent as <c>Harvest-Account-Id</c> header).</summary>
    public string? AccountId { get; set; }
    /// <summary>Personal access token (sent as <c>Authorization: Bearer ...</c>).</summary>
    public string? AccessToken { get; set; }
    /// <summary>Default project ID to use when an intent doesn't specify one.</summary>
    public long? DefaultProjectId { get; set; }
    /// <summary>Default task ID to use when an intent doesn't specify one.</summary>
    public long? DefaultTaskId { get; set; }
}

public sealed class TrelloSecrets
{
    public string? ApiKey { get; set; }
    public string? Token { get; set; }
    /// <summary>Default list ID — Trello cards must belong to a list.</summary>
    public string? DefaultListId { get; set; }
}

public sealed class SlackSecrets
{
    /// <summary>Bot user OAuth token (<c>xoxb-...</c>) with at minimum <c>chat:write</c>.</summary>
    public string? BotToken { get; set; }
    /// <summary>Default channel (e.g. <c>#general</c> or a channel ID like <c>C0123456</c>).</summary>
    public string? DefaultChannel { get; set; }
}
