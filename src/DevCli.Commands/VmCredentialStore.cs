using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DevCli.Commands;

/// <summary>
/// Port of Scripts/vm Get/Set-CaptureVMCredential.ps1: DPAPI-encrypted
/// password store (%APPDATA%/&lt;app&gt;/vmcred-&lt;vm&gt;.txt, CurrentUser
/// scope — only readable by the storing Windows account, never plaintext)
/// plus the reset-password generator. Secrets travel in SecureString/memory
/// only and are never logged.
///
/// Wire format = PowerShell ConvertFrom-SecureString (DPAPI blob as
/// continuous lowercase hex, plaintext UTF-16LE), so deco and the .ps1 kit
/// share the SAME file. Legacy deco base64/UTF-8 blobs still read.
/// </summary>
public static partial class VmCredentialStore
{
    [GeneratedRegex(@"^[abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789]+$")]
    private static partial Regex PasswordAlphabet();

    public const string PasswordAlphabetChars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static string StorePath(string appId, string vmName, string? storeDir = null)
    {
        string dir = storeDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appId);
        return Path.Combine(dir, $"vmcred-{vmName}.txt");
    }

    /// <summary>Resolution order: --password flag, else the DPAPI store (Windows-only).</summary>
    public static string ResolvePassword(string flagPassword, string appId, string vmName, string? storeDir = null)
    {
        if (!string.IsNullOrWhiteSpace(flagPassword))
        {
            return flagPassword;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new Core.DecoException(
                "The DPAPI credential store requires Windows; pass --password instead.",
                "windows-only",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        return ReadStoreWindows(appId, vmName, storeDir);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string ReadStoreWindows(string appId, string vmName, string? storeDir)
    {
        string path = StorePath(appId, vmName, storeDir);
        if (!File.Exists(path))
        {
            throw new Core.DecoException(
                $"No VM password: pass --password, or store it once via deco vm credential --vm {vmName} (secure prompt, nothing in plaintext).",
                "vm-no-password",
                Core.ExitCodes.UsageOrAuthNeeded);
        }

        try
        {
            string stored = File.ReadAllText(path, Encoding.UTF8).Trim();
            return DecodeStoreText(stored);
        }
        catch (Core.DecoException)
        {
            throw;
        }
        catch
        {
            throw new Core.DecoException(
                $"Stored credential for '{vmName}' is unreadable (corrupt file?). Delete it and re-run: deco vm credential --vm {vmName}",
                "vm-credential-unreadable");
        }
    }

    /// <summary>
    /// Decodes a store file: SecureString-hex first (the ps1/deco format),
    /// legacy deco base64/UTF-8 second. Public and tested.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string DecodeStoreText(string stored)
    {
        // Format 1: ConvertFrom-SecureString — DPAPI blob as hex, UTF-16LE plaintext.
        if (IsHexText(stored))
        {
            try
            {
                byte[] protectedBytes = Convert.FromHexString(stored);
                byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                try
                {
                    return Encoding.Unicode.GetString(plain);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
            catch
            {
                // Fall through to legacy format.
            }
        }

        // Format 2: legacy deco — base64 blob, UTF-8 plaintext.
        byte[] legacyProtected = Convert.FromBase64String(stored);
        byte[] legacyPlain = ProtectedData.Unprotect(legacyProtected, null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(legacyPlain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacyPlain);
        }
    }

    private static bool IsHexText(string s) =>
        s.Length >= 32 && s.Length % 2 == 0
        && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

    /// <summary>
    /// Writes the DPAPI store in ConvertFrom-SecureString format (shared with
    /// the .ps1 kit). Windows-only (throws elsewhere).
    /// </summary>
    public static void SavePassword(string password, string appId, string vmName, string? storeDir = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new Core.DecoException("The DPAPI credential store requires Windows.", "windows-only");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new Core.DecoException("Password must not be empty.", "vm-empty-password", Core.ExitCodes.UsageOrAuthNeeded);
        }

        byte[] plain = Encoding.Unicode.GetBytes(password);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            string path = StorePath(appId, vmName, storeDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Convert.ToHexString(protectedBytes).ToLowerInvariant(), new UTF8Encoding(false));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static string RandomPassword(int length = 20)
    {
        var sb = new StringBuilder(length);
        foreach (byte b in RandomNumberGenerator.GetBytes(length))
        {
            sb.Append(PasswordAlphabetChars[b % PasswordAlphabetChars.Length]);
        }

        return sb.ToString();
    }

    public static bool IsRandomPassword(string password) =>
        password.Length == 20 && PasswordAlphabet().IsMatch(password);
}
