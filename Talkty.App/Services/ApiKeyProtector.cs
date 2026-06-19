using System.Security.Cryptography;
using System.Text;

namespace Talkty.App.Services;

/// <summary>
/// Encrypts/decrypts secrets (e.g. the OpenRouter API key) for at-rest storage in
/// settings.json. Uses Windows DPAPI (CurrentUser scope) so the ciphertext is only
/// decryptable by the same Windows user on the same machine — the key is never
/// written to disk in plaintext.
/// </summary>
public static class ApiKeyProtector
{
    // Tag prepended to ciphertext so we can recognize an already-protected value
    // and stay backward/forward compatible if the scheme changes.
    private const string Prefix = "dpapi:";

    /// <summary>
    /// Encrypts a plaintext secret to a storable string. Returns null/empty unchanged.
    /// </summary>
    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var encrypted = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to protect secret — storing is aborted", ex);
            return null;
        }
    }

    /// <summary>
    /// Decrypts a stored secret back to plaintext. Tolerates unprotected legacy values
    /// (returns them as-is) and returns null on failure.
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return stored;

        // Value was stored before protection existed (or hand-edited) — pass through.
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;

        try
        {
            var base64 = stored[Prefix.Length..];
            var encrypted = Convert.FromBase64String(base64);
            var bytes = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to unprotect secret — treating as missing", ex);
            return null;
        }
    }
}
