// This file is intentionally retained as a compile-friendly stub.
//
// The pre-Phase-4 plain-JSON SecretsLoader has been superseded by
// WorkIntel.Configuration.PreferencesStore, which loads a DPAPI-encrypted
// preferences blob and overlays env vars for blank fields.
//
// Existing integrations of SecretsLoader.Load() should switch to
// PreferencesStore.Load(), then read .Secrets off the returned AppPreferences.
//
// This stub stays here so any in-flight branches still compile during the
// migration; it can be deleted in a follow-up cleanup commit.

using WorkIntel.Configuration;

namespace WorkIntel.Integrations;

[System.Obsolete("Use WorkIntel.Configuration.PreferencesStore.Load() and read AppPreferences.Secrets.", error: false)]
public static class SecretsLoader
{
    public static IntegrationSecrets Load() => PreferencesStore.Load().Secrets;
}
