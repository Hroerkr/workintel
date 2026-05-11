using System;
using System.IO;
using WorkIntel.Configuration;
using WorkIntel.Integrations;
using WorkIntel.Intent;
using WorkIntel.Transcription;
using Xunit;

namespace WorkIntel.Tests;

/// <summary>
/// Round-trip tests for the DPAPI-encrypted preferences store. Windows-only —
/// <see cref="System.Security.Cryptography.ProtectedData"/> isn't available
/// on Linux/macOS, but the project itself is Windows-only so this is fine.
/// </summary>
public sealed class PreferencesStoreTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _prefsPath;

    public PreferencesStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "WorkIntel.Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _prefsPath = Path.Combine(_tmpDir, "preferences.dat");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var prefs = PreferencesStore.Load(_prefsPath);

        Assert.Equal("1", prefs.SchemaVersion);
        Assert.Equal(WhisperModel.BaseEn, prefs.Whisper.Model);
        Assert.Equal(LocalLlmModel.Phi35MiniInstructQ4, prefs.Llm.Model);
    }

    [Fact]
    public void SaveLoad_RoundTrip_PreservesAllFields()
    {
        var original = new AppPreferences
        {
            SchemaVersion = "1",
            Capture = new CapturePreferences
            {
                IdleAfterSeconds = 120,
                VadActivationDb = -38f,
                VadDeactivationDb = -48f,
                VadHangoverMs = 900,
            },
            Whisper = new WhisperOptions
            {
                Model = WhisperModel.SmallEn,
                Language = "en",
                InitialPrompt = "Acme, Bertha, Crispr",
                Translate = true,
                Threads = 6,
            },
            Llm = new LocalLlmOptions
            {
                Model = LocalLlmModel.Phi35MiniInstructQ5,
                ContextSize = 8192,
                Threads = 8,
                GpuLayerCount = 33,
                Temperature = 0.05f,
                MaxOutputTokens = 1024,
            },
            Secrets = new IntegrationSecrets
            {
                Harvest = new HarvestSecrets
                {
                    AccountId = "12345",
                    AccessToken = "harvest-pat-abc",
                    DefaultProjectId = 999_888_777,
                    DefaultTaskId = 111_222_333,
                },
                Trello = new TrelloSecrets
                {
                    ApiKey = "key",
                    Token = "token",
                    DefaultListId = "abc123",
                },
                Slack = new SlackSecrets
                {
                    BotToken = "xoxb-secret",
                    DefaultChannel = "#eng-build",
                }
            }
        };

        PreferencesStore.Save(original, _prefsPath);
        var loaded = PreferencesStore.Load(_prefsPath, legacyConfigPath: null);

        Assert.Equal(original.SchemaVersion, loaded.SchemaVersion);

        Assert.Equal(original.Capture.IdleAfterSeconds, loaded.Capture.IdleAfterSeconds);
        Assert.Equal(original.Capture.VadActivationDb, loaded.Capture.VadActivationDb);
        Assert.Equal(original.Capture.VadDeactivationDb, loaded.Capture.VadDeactivationDb);
        Assert.Equal(original.Capture.VadHangoverMs, loaded.Capture.VadHangoverMs);

        Assert.Equal(original.Whisper.Model, loaded.Whisper.Model);
        Assert.Equal(original.Whisper.Language, loaded.Whisper.Language);
        Assert.Equal(original.Whisper.InitialPrompt, loaded.Whisper.InitialPrompt);
        Assert.Equal(original.Whisper.Translate, loaded.Whisper.Translate);
        Assert.Equal(original.Whisper.Threads, loaded.Whisper.Threads);

        Assert.Equal(original.Llm.Model, loaded.Llm.Model);
        Assert.Equal(original.Llm.ContextSize, loaded.Llm.ContextSize);
        Assert.Equal(original.Llm.Threads, loaded.Llm.Threads);
        Assert.Equal(original.Llm.GpuLayerCount, loaded.Llm.GpuLayerCount);
        Assert.Equal(original.Llm.Temperature, loaded.Llm.Temperature);

        Assert.Equal(original.Secrets.Harvest!.AccountId, loaded.Secrets.Harvest!.AccountId);
        Assert.Equal(original.Secrets.Harvest.AccessToken, loaded.Secrets.Harvest.AccessToken);
        Assert.Equal(original.Secrets.Harvest.DefaultProjectId, loaded.Secrets.Harvest.DefaultProjectId);
        Assert.Equal(original.Secrets.Harvest.DefaultTaskId, loaded.Secrets.Harvest.DefaultTaskId);

        Assert.Equal(original.Secrets.Trello!.ApiKey, loaded.Secrets.Trello!.ApiKey);
        Assert.Equal(original.Secrets.Trello.Token, loaded.Secrets.Trello.Token);
        Assert.Equal(original.Secrets.Trello.DefaultListId, loaded.Secrets.Trello.DefaultListId);

        Assert.Equal(original.Secrets.Slack!.BotToken, loaded.Secrets.Slack!.BotToken);
        Assert.Equal(original.Secrets.Slack.DefaultChannel, loaded.Secrets.Slack.DefaultChannel);
    }

    [Fact]
    public void OnDiskFile_ContainsCipherAndIsNotPlaintextSecret()
    {
        var prefs = new AppPreferences
        {
            Secrets = new IntegrationSecrets
            {
                Slack = new SlackSecrets { BotToken = "xoxb-VERY-SECRET-12345" }
            }
        };

        PreferencesStore.Save(prefs, _prefsPath);
        var fileText = File.ReadAllText(_prefsPath);

        // Wrapper is plaintext, payload is encrypted.
        Assert.Contains("encryptedData", fileText);
        Assert.DoesNotContain("xoxb-VERY-SECRET-12345", fileText);
    }

    [Fact]
    public void Save_OverwritesExistingFile_Atomically()
    {
        PreferencesStore.Save(new AppPreferences { SchemaVersion = "1" }, _prefsPath);
        var first = File.ReadAllText(_prefsPath);

        PreferencesStore.Save(new AppPreferences { SchemaVersion = "1" }, _prefsPath);
        var second = File.ReadAllText(_prefsPath);

        Assert.True(File.Exists(_prefsPath));
        // Different ciphertexts (DPAPI uses a random IV) — verify we replaced the file.
        Assert.NotEqual(first, second);
        Assert.False(File.Exists(_prefsPath + ".tmp"), "tmp file should not be left behind");
    }

    [Fact]
    public void LegacyMigration_ImportsConfigJson_AndBacksItUp()
    {
        var legacyPath = Path.Combine(_tmpDir, "config.json");
        File.WriteAllText(legacyPath, """
            {
              "harvest": { "accountId": "777", "accessToken": "legacy-pat" },
              "slack":   { "botToken": "xoxb-legacy", "defaultChannel": "#leg" }
            }
            """);

        var loaded = PreferencesStore.Load(_prefsPath, legacyConfigPath: legacyPath);

        Assert.Equal("777", loaded.Secrets.Harvest?.AccountId);
        Assert.Equal("legacy-pat", loaded.Secrets.Harvest?.AccessToken);
        Assert.Equal("xoxb-legacy", loaded.Secrets.Slack?.BotToken);
        Assert.Equal("#leg", loaded.Secrets.Slack?.DefaultChannel);

        Assert.False(File.Exists(legacyPath), "legacy file should be renamed away");
        Assert.True(File.Exists(legacyPath + ".migrated.bak"));
        Assert.True(File.Exists(_prefsPath));
    }
}
