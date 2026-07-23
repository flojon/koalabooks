using System.Security.Cryptography;
using System.Text;

namespace KoalaBooks.Tests;

/// <summary>RFC 6238 TOTP, computed independently of ASP.NET Identity's provider to avoid testing the mock against itself.</summary>
internal static class TotpTestHelper
{
    /// <summary>
    /// Generates a code with a safety margin against the 30s step boundary: if the current
    /// step is about to roll over, waits for the next one first so the code stays valid
    /// through the network/DB round trips the caller still has to make before the server
    /// verifies it.
    /// </summary>
    public static async Task<string> GenerateCodeAsync(string base32Secret)
    {
        var secondsIntoStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 30;
        if (secondsIntoStep > 25)
            await Task.Delay(TimeSpan.FromSeconds(30 - secondsIntoStep + 1));

        return GenerateCode(base32Secret);
    }

    private static string GenerateCode(string base32Secret)
    {
        var secretBytes = Base32Decode(base32Secret);
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(secretBytes);
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0F;
        var binaryCode = ((hash[offset] & 0x7F) << 24)
                        | ((hash[offset + 1] & 0xFF) << 16)
                        | ((hash[offset + 2] & 0xFF) << 8)
                        | (hash[offset + 3] & 0xFF);
        return (binaryCode % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        input = input.TrimEnd('=').ToUpperInvariant();
        var bits = new StringBuilder();
        foreach (var c in input)
            bits.Append(Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0'));

        var bytes = new List<byte>();
        for (var i = 0; i + 8 <= bits.Length; i += 8)
            bytes.Add(Convert.ToByte(bits.ToString(i, 8), 2));
        return bytes.ToArray();
    }
}
