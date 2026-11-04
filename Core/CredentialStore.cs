using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskTwin.Core;

public sealed record CredentialInput(string UserName, string Password, bool Remember);

public sealed record SavedCredential(string UserName, string Password);

public static class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DeskTwin.Credentials.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskTwin",
        "credentials.dat");

    public static bool HasSavedCredential => File.Exists(FilePath);

    public static void Save(string userName, string password) =>
        SaveTo(FilePath, userName, password);

    internal static void SaveTo(string filePath, string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new ArgumentException("账户不能为空。", nameof(userName));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("密码不能为空。", nameof(password));
        }

        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("无法确定凭据目录。");
        Directory.CreateDirectory(directory);

        var protectedPassword = Protect(password);
        var serialized = JsonSerializer.Serialize(
            new CredentialRecord(userName.Trim(), protectedPassword),
            JsonOptions);
        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, serialized, new UTF8Encoding(false));
        File.Move(temporaryPath, filePath, true);
    }

    public static SavedCredential? Load() => LoadFrom(FilePath);

    internal static SavedCredential? LoadFrom(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var record = JsonSerializer.Deserialize<CredentialRecord>(
                File.ReadAllText(filePath), JsonOptions);
            if (record is null
                || string.IsNullOrWhiteSpace(record.UserName)
                || string.IsNullOrWhiteSpace(record.ProtectedPassword))
            {
                return null;
            }

            var password = Unprotect(record.ProtectedPassword);
            return string.IsNullOrEmpty(password)
                ? null
                : new SavedCredential(record.UserName, password);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or CryptographicException
                                          or FormatException)
        {
            return null;
        }
    }

    public static void Delete() => DeleteFile(FilePath);

    internal static void DeleteFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    internal static string Protect(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                plaintextBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    internal static string Unprotect(string protectedBase64)
    {
        var protectedBytes = Convert.FromBase64String(protectedBase64);
        try
        {
            var plaintextBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plaintextBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private sealed record CredentialRecord(string UserName, string ProtectedPassword);
}
