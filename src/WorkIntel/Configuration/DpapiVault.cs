using System.Security.Cryptography;

namespace WorkIntel.Configuration;

/// <summary>
/// Thin wrapper over Windows DPAPI (<see cref="ProtectedData"/>). Encrypted blobs
/// are scoped to <see cref="DataProtectionScope.CurrentUser"/> — i.e. only the
/// same Windows user account on this machine can decrypt them. Suitable for
/// a desktop tray app's local credentials store.
/// </summary>
/// <remarks>
/// DPAPI is deliberately opinionated: no key management for the caller, no
/// bring-your-own-cipher escape hatch. That keeps the surface tiny — but it also
/// means the on-disk file is non-portable. If a user backs up <c>%LOCALAPPDATA%</c>
/// and restores onto a new machine, the encrypted blob will fail to decrypt and
/// they'll need to re-enter credentials.
/// </remarks>
public static class DpapiVault
{
    /// <summary>Optional entropy mixed in alongside the user master key. Tightens
    /// scope to "this app on this user account" so other apps running as the same
    /// user can't trivially decrypt our blob via DPAPI.</summary>
    private static readonly byte[] AppEntropy =
        System.Text.Encoding.UTF8.GetBytes("WorkIntel/v1");

    public static byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, AppEntropy, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, AppEntropy, DataProtectionScope.CurrentUser);
}
