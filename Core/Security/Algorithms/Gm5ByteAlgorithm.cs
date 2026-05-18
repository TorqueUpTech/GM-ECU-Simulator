using System.Security.Cryptography;
using System.Text.Json;

namespace Core.Security.Algorithms;

// The GMW3110 5-byte SecurityAccess algorithm. One algorithm parameterised by
// a per-algoId password supplied by Gm5BytePasswords; the cipher math itself
// is identical for every algoId.
//
// Math:
//   1. password is a 62-char ASCII blob: "01" or "03" (length marker)
//      followed by 60 chars of base64 (44 decoded bytes).
//   2. blob[0..31]  = SHA-256 chain payload (32 bytes)
//      blob[32..33] = WORD A    (big-endian, governs iteration count)
//      blob[34..35] = WORD B    (big-endian, must equal algoId)
//      blob[36..43] = 8-byte signature (not validated here)
//   3. N = 0xFF - seed[4] - A
//   4. h = payload; repeat N times: h = SHA-256(h)
//   5. aes_key = h[0..15]
//   6. plaintext  = [0xFF * 11 || seed[0..4]]
//   7. ciphertext = AES-128-ECB-Encrypt(aes_key, plaintext)
//   8. response_key = ciphertext[0..4]
//
// Configuration (optional, via SecurityModuleConfig JsonElement):
//   {
//     "algoId":     "0x92",         // hex byte; default 0x92
//     "password":   "01...zQk=",     // 62-char ASCII override; default = table entry
//     "familyByte": "0x06",         // seed[4]; default 0x06 (gives N=4)
//     "fixedSeed":  "1122334406"     // 10 hex chars; default random with seed[4]=familyByte
//   }
public sealed class Gm5ByteAlgorithm : ISeedKeyAlgorithm
{
    public const byte DefaultFamilyByte = 0x06;
    public const int  DefaultAlgoId     = 0x92;

    public static string DefaultAlgo92Password => Gm5BytePasswords.Table[0x92];

    private byte[]? fixedSeed;
    private string  password;
    private int     algoId;
    private byte    familyByte;

    public Gm5ByteAlgorithm()
    {
        algoId     = DefaultAlgoId;
        familyByte = DefaultFamilyByte;
        password   = Gm5BytePasswords.Table[algoId];
    }

    public string Id => "gm-e92-5byte";
    public int SeedLength => 5;
    public int KeyLength  => 5;
    public IEnumerable<byte> SupportedLevels { get; } = new byte[] { 1 };

    public void GenerateSeed(byte level, Span<byte> seedBuffer, out int seedLength)
    {
        if (fixedSeed is not null)
        {
            fixedSeed.AsSpan().CopyTo(seedBuffer);
        }
        else
        {
            Random.Shared.NextBytes(seedBuffer.Slice(0, 5));
            // seed[4] is the family byte; the tester rejects mismatches.
            seedBuffer[4] = familyByte;
        }
        seedLength = 5;
    }

    public bool ComputeExpectedKey(byte level, ReadOnlySpan<byte> seed, Span<byte> keyBuffer, out int keyLength)
    {
        if (level != 1 || seed.Length != 5)
        {
            keyLength = 0;
            return false;
        }
        try
        {
            ComputeKey(seed, algoId, password, keyBuffer.Slice(0, 5));
            keyLength = 5;
            return true;
        }
        catch
        {
            // Malformed password / config mismatch - surface as InvalidKey upstream.
            keyLength = 0;
            return false;
        }
    }

    public void LoadConfig(JsonElement? config)
    {
        fixedSeed  = null;
        algoId     = DefaultAlgoId;
        familyByte = DefaultFamilyByte;
        password   = Gm5BytePasswords.Table[algoId];

        if (config is null || config.Value.ValueKind != JsonValueKind.Object) return;

        if (config.Value.TryGetProperty("algoId", out var pAlgo) &&
            pAlgo.ValueKind == JsonValueKind.String &&
            TryParseHexUInt(pAlgo.GetString(), out var aId) && aId <= 0xFF)
        {
            algoId = (int)aId;
            // Default the password to the table entry for the new algoId
            // unless the caller explicitly overrides it below.
            password = Gm5BytePasswords.Table[algoId];
        }

        if (config.Value.TryGetProperty("password", out var pPwd) &&
            pPwd.ValueKind == JsonValueKind.String)
        {
            var p = pPwd.GetString();
            if (!string.IsNullOrEmpty(p) && p.Length == 62) password = p;
        }

        if (config.Value.TryGetProperty("familyByte", out var pFam) &&
            pFam.ValueKind == JsonValueKind.String &&
            TryParseHexUInt(pFam.GetString(), out var fb) && fb <= 0xFF)
            familyByte = (byte)fb;

        if (config.Value.TryGetProperty("fixedSeed", out var pSeed) &&
            pSeed.ValueKind == JsonValueKind.String)
        {
            var bytes = ParseHexBytes(pSeed.GetString(), 5);
            if (bytes is not null) fixedSeed = bytes;
        }
    }

    /// <summary>
    /// The bare math. Throws on malformed password or algoId mismatch.
    /// Useful for unit tests; the production path goes through
    /// <see cref="ComputeExpectedKey"/> which catches and reports InvalidKey.
    /// </summary>
    public static void ComputeKey(ReadOnlySpan<byte> seed, int algoId, string password, Span<byte> outKey)
    {
        if (seed.Length != 5) throw new ArgumentException("seed must be 5 bytes", nameof(seed));
        if (outKey.Length < 5) throw new ArgumentException("outKey must be >= 5 bytes", nameof(outKey));
        if (password is null || password.Length != 62)
            throw new ArgumentException("password must be 62 ASCII chars (2 prefix + 60 base64)", nameof(password));

        // Length marker
        if (!int.TryParse(password.AsSpan(0, 2), out var marker) || (marker != 1 && marker != 3))
            throw new InvalidOperationException("password length marker must be \"01\" or \"03\"");

        var blob = Convert.FromBase64String(password.Substring(2, 60));
        if (blob.Length < 44) throw new InvalidOperationException("password base64 decoded too short");

        var payload = blob.AsSpan(0, 32);
        int A = (blob[32] << 8) | blob[33];
        int B = (blob[34] << 8) | blob[35];
        // blob[36..43] = signature; not validated here. A wrong password
        // produces a wrong AES key downstream and the computed response key
        // will mismatch the tester's, which is the practical detection path.

        if (B != algoId)
            throw new InvalidOperationException(
                $"password's embedded algoId 0x{B:X4} != requested 0x{algoId:X4}");
        if (A > 0xFF - seed[4])
            throw new InvalidOperationException("A would underflow iteration count");

        int nIter = 0xFF - seed[4] - A;

        Span<byte> h = stackalloc byte[32];
        payload.CopyTo(h);
        Span<byte> tmp = stackalloc byte[32];
        for (int i = 0; i < nIter; i++)
        {
            SHA256.HashData(h, tmp);
            tmp.CopyTo(h);
        }

        Span<byte> plaintext = stackalloc byte[16];
        plaintext.Slice(0, 11).Fill(0xFF);
        seed.CopyTo(plaintext.Slice(11));

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.KeySize = 128;
        aes.Key = h.Slice(0, 16).ToArray();
        Span<byte> ciphertext = stackalloc byte[16];
        aes.EncryptEcb(plaintext, ciphertext, PaddingMode.None);

        ciphertext.Slice(0, 5).CopyTo(outKey);
    }

    private static bool TryParseHexUInt(string? s, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return uint.TryParse(t, System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static byte[]? ParseHexBytes(string? s, int expectedLength)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().Replace(" ", "").Replace("-", "");
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (t.Length != expectedLength * 2) return null;
        var bytes = new byte[expectedLength];
        for (int i = 0; i < expectedLength; i++)
        {
            if (!byte.TryParse(t.AsSpan(i * 2, 2),
                               System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture,
                               out bytes[i]))
                return null;
        }
        return bytes;
    }
}
