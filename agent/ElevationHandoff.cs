using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteAssist.Agent;

internal sealed record ElevationHandoff(
    string Server,
    string SessionId,
    string AgentToken,
    string WebSocketUrl,
    DateTime LiveExpiresAtUtc,
    bool RequestedControl,
    bool RequestedClipboard,
    bool RequestedFileTransfer)
{
    private static readonly byte[] AdditionalEntropy =
        Encoding.UTF8.GetBytes("Remote Assist.Support.ElevationHandoff.v1");

    public static string Write(ElevationHandoff handoff)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Remote Assist",
            "Support");

        Directory.CreateDirectory(root);

        var path = Path.Combine(
            root,
            $"elevation-{Guid.NewGuid():N}.bin");

        var json = JsonSerializer.Serialize(handoff);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var encrypted = ProtectedData.Protect(
            plaintext,
            AdditionalEntropy,
            DataProtectionScope.CurrentUser);

        try
        {
            File.WriteAllBytes(path, encrypted);
            File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Temporary);
            return path;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public static ElevationHandoff ReadAndDelete(string path)
    {
        byte[]? encrypted = null;
        byte[]? plaintext = null;

        try
        {
            encrypted = File.ReadAllBytes(path);
            plaintext = ProtectedData.Unprotect(
                encrypted,
                AdditionalEntropy,
                DataProtectionScope.CurrentUser);

            var json = Encoding.UTF8.GetString(plaintext);
            return JsonSerializer.Deserialize<ElevationHandoff>(json)
                ?? throw new InvalidOperationException(
                    "Elevation handoff is empty.");
        }
        finally
        {
            if (encrypted is not null)
                CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);

            try
            {
                File.Delete(path);
            }
            catch { }
        }
    }
}
