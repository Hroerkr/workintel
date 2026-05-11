using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkIntel.App;
using WorkIntel.Integrations;

namespace WorkIntel.Configuration;

/// <summary>
/// Loads and saves the encrypted <see cref="AppPreferences"/> blob.
/// </summary>
/// <remarks>
/// <para>
/// Disk layout: a small JSON wrapper at <c>%LOCALAPPDATA%\WorkIntel\preferences.dat</c>
/// containing the schema version, save timestamp, and a base64-encoded DPAPI
/// ciphertext. The wrapper is deliberately not encrypted itself so a curious
/// user can inspect "what version" without touching DPAPI.
/// </para>
/// <para>
/// Migration: if the legacy plaintext <c>config.json</c> is present, its
/// integration secrets are imported on first load and the file is renamed to
/// <c>config.json.migrated.bak</c>. Subsequent runs ignore it.
/// </para>
/// <para>
/// Env-var overlay: any blank secret field in the loaded preferences gets
/// filled from <c>WORKINTEL_*</c> environment variables. Useful for headless
/// / CI setups where DPAPI can't help. Env-var values are *not* persisted —
/// they're applied per-load.
/// </para>
/// </remarks>
public static class PreferencesStore
{
    public static string FilePath { get; } = Path.Combine(Log.LogDirectory, "preferences.dat");
    private static string DefaultLegacyConfigPath { get; } = Path.Combine(Log.LogDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppPreferences Load() => Load(FilePath, DefaultLegacyConfigPath);

    /// <summary>Test-friendly overload that lets callers redirect both the encrypted
    /// preferences path and the legacy plaintext path to a temporary location.</summary>
    public static AppPreferences Load(string filePath, string? legacyConfigPath = null)
    {
        AppPreferences prefs;

        try
        {
            if (File.Exists(filePath))
            {
                prefs = ReadEncrypted(filePath);
            }
            else
            {
                prefs = new AppPreferences();
                if (!string.IsNullOrEmpty(legacyConfigPath))
                    TryMigrateLegacy(prefs, filePath, legacyConfigPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error("preferences load failed; falling back to defaults", ex);
            prefs = new AppPreferences();
        }

        OverlayEnvVars(prefs.Secrets);
        LogSummary(prefs);
        return prefs;
    }

    public static void Save(AppPreferences prefs) => Save(prefs, FilePath);

    /// <summary>Test-friendly overload that writes to an arbitrary path.</summary>
    public static void Save(AppPreferences prefs, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var inner = JsonSerializer.Serialize(prefs, JsonOpts);
        var cipher = DpapiVault.Protect(Encoding.UTF8.GetBytes(inner));
        var wrapper = new EncryptedFile
        {
            SchemaVersion = prefs.SchemaVersion,
            SavedAt = DateTimeOffset.UtcNow.ToString("O"),
            EncryptedData = Convert.ToBase64String(cipher),
        };
        var outer = JsonSerializer.Serialize(wrapper, JsonOpts);

        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, outer);
        if (File.Exists(filePath)) File.Delete(filePath);
        File.Move(tmp, filePath);
        Log.Info($"preferences saved to {filePath}");
    }

    private static AppPreferences ReadEncrypted(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var wrapper = JsonSerializer.Deserialize<EncryptedFile>(json, JsonOpts)
            ?? throw new InvalidDataException("preferences wrapper failed to parse");

        if (string.IsNullOrEmpty(wrapper.EncryptedData))
            throw new InvalidDataException("preferences wrapper missing encryptedData");

        var cipher = Convert.FromBase64String(wrapper.EncryptedData);
        var plain = DpapiVault.Unprotect(cipher);
        var inner = Encoding.UTF8.GetString(plain);
        return JsonSerializer.Deserialize<AppPreferences>(inner, JsonOpts) ?? new AppPreferences();
    }

    private static void TryMigrateLegacy(AppPreferences prefs, string targetFilePath, string legacyConfigPath)
    {
        if (!File.Exists(legacyConfigPath)) return;

        try
        {
            var json = File.ReadAllText(legacyConfigPath);
            var legacy = JsonSerializer.Deserialize<IntegrationSecrets>(json, JsonOpts);
            if (legacy is not null) prefs.Secrets = legacy;
            Save(prefs, targetFilePath); // encrypt + persist before retiring the legacy file

            var backup = legacyConfigPath + ".migrated.bak";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(legacyConfigPath, backup);

            Log.Info($"migrated legacy config.json → encrypted preferences (backup at {backup})");
        }
        catch (Exception ex)
        {
            Log.Warn($"legacy migration failed: {ex.Message}");
        }
    }

    private static void OverlayEnvVars(IntegrationSecrets secrets)
    {
        secrets.Harvest ??= new HarvestSecrets();
        secrets.Trello  ??= new TrelloSecrets();
        secrets.Slack   ??= new SlackSecrets();

        if (string.IsNullOrWhiteSpace(secrets.Harvest.AccountId))   secrets.Harvest.AccountId   = Env("WORKINTEL_HARVEST_ACCOUNT_ID");
        if (string.IsNullOrWhiteSpace(secrets.Harvest.AccessToken)) secrets.Harvest.AccessToken = Env("WORKINTEL_HARVEST_TOKEN");
        secrets.Harvest.DefaultProjectId ??= EnvLong("WORKINTEL_HARVEST_DEFAULT_PROJECT_ID");
        secrets.Harvest.DefaultTaskId    ??= EnvLong("WORKINTEL_HARVEST_DEFAULT_TASK_ID");

        if (string.IsNullOrWhiteSpace(secrets.Trello.ApiKey))        secrets.Trello.ApiKey        = Env("WORKINTEL_TRELLO_KEY");
        if (string.IsNullOrWhiteSpace(secrets.Trello.Token))         secrets.Trello.Token         = Env("WORKINTEL_TRELLO_TOKEN");
        if (string.IsNullOrWhiteSpace(secrets.Trello.DefaultListId)) secrets.Trello.DefaultListId = Env("WORKINTEL_TRELLO_DEFAULT_LIST");

        if (string.IsNullOrWhiteSpace(secrets.Slack.BotToken))       secrets.Slack.BotToken       = Env("WORKINTEL_SLACK_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(secrets.Slack.DefaultChannel)) secrets.Slack.DefaultChannel = Env("WORKINTEL_SLACK_DEFAULT_CHANNEL") ?? "#general";
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static long? EnvLong(string name) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : null;

    private static void LogSummary(AppPreferences prefs)
    {
        string Status(string? token) => string.IsNullOrWhiteSpace(token) ? "absent" : "present";
        Log.Info(
            "preferences loaded — " +
            $"schema={prefs.SchemaVersion}, " +
            $"whisper={prefs.Whisper.Model}, llm={prefs.Llm.Model}, " +
            $"harvest={Status(prefs.Secrets.Harvest?.AccessToken)}, " +
            $"trello={Status(prefs.Secrets.Trello?.Token)}, " +
            $"slack={Status(prefs.Secrets.Slack?.BotToken)}");
    }

    private sealed class EncryptedFile
    {
        public string? SchemaVersion { get; set; }
        public string? SavedAt { get; set; }
        public string? EncryptedData { get; set; }
    }
}
