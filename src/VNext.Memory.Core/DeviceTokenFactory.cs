using System.Security.Cryptography;
using System.Text;

namespace VNext.Memory.Core;

public static class DeviceTokenFactory
{
    public static (string RawToken, string TokenHash) Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = "vme_dev_" + Base64UrlEncode(bytes);
        var tokenHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)))
            .ToLowerInvariant();

        return (rawToken, tokenHash);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
