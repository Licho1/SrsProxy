using System.Security.Cryptography;
using System.Text;

namespace SrsProxy;

public static class SrsRewriter
{
    static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    const string Base32Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string SrsForward(string sender, string srsDomain, string secret)
    {
        var atIndex = sender.IndexOf('@');
        if (atIndex < 0) throw new ArgumentException($"Invalid sender address: {sender}");

        var user = sender[..atIndex];
        var domain = sender[(atIndex + 1)..];

        var timestamp = EncodeTimestamp(DateTime.UtcNow);
        var hash = ComputeHash(secret, $"{timestamp}{domain}{user}");

        return $"SRS0={hash}={timestamp}={domain}={user}@{srsDomain}";
    }

    public static string SrsReverse(string srsAddress, string secret)
    {
        var atIndex = srsAddress.IndexOf('@');
        if (atIndex < 0) throw new ArgumentException($"Invalid SRS address: {srsAddress}");

        var localPart = srsAddress[..atIndex];
        if (!localPart.StartsWith("SRS0=", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Not an SRS0 address: {srsAddress}");

        var parts = localPart[5..].Split('=', 4);
        if (parts.Length != 4)
            throw new ArgumentException($"Malformed SRS0 address: {srsAddress}");

        var (hash, timestamp, domain, user) = (parts[0], parts[1], parts[2], parts[3]);

        var expectedHash = ComputeHash(secret, $"{timestamp}{domain}{user}");
        if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SRS hash verification failed");

        return $"{user}@{domain}";
    }

    static string EncodeTimestamp(DateTime dt)
    {
        var days = (int)(dt - Epoch).TotalDays % 1024;
        return new string([Base32Chars[days / 32], Base32Chars[days % 32]]);
    }

    static string ComputeHash(string secret, string data)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var message = Encoding.UTF8.GetBytes(data.ToLowerInvariant());
        var hash = HMACSHA1.HashData(key, message);
        return Convert.ToBase64String(hash)[..4];
    }
}
