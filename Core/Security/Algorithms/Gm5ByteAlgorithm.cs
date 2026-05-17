using System.Security.Cryptography;
using System.Text.Json;

namespace Core.Security.Algorithms;

// GM DPS 4.52 "Enhanced 5-byte" seed/key algorithm. This is what DPS's UI
// calls "Algo 92" for E92-family ECMs (and the same algorithm wraps any other
// 5-byte algoId DPS supports - the per-algorithm "password" is what differs).
//
// Reverse-engineered 2026-05-17 by hooking C:\DPS\sa015bcr.dll's `sa015bcr`
// export with a logging proxy (tools/sa015bcr_hook/). The chain is:
//
//   dpsvcs.dll!FUN_1009eb40 (UnlockDispatcher)
//     -> LoadLibraryW(<DPS_Root>\IVCS5B.dll)
//     -> GetProcAddress("RequestOperation")
//   IVCS5B.dll!RequestOperation
//     -> sale.dll!fnRequestOperation(op=0x83, ...)   // license check, decrypts
//                                                    // per-algo password table
//                                                    // in IVCS5B.dll's .data
//                                                    // section in-place
//     -> sa015bcr.dll!sa015bcr(seed, password, algoId, out)   // the math
//
// The algorithm:
//   1. password is a 62-char ASCII blob: "01" or "03" (length marker) followed
//      by 60 chars of base64 (= 44 decoded bytes).
//   2. blob[0..31]  = SHA-256 chain seed payload (32 bytes)
//      blob[32..33] = WORD A (big-endian)         - controls iteration count
//      blob[34..35] = WORD B (big-endian)         - must equal algoId
//      blob[36..43] = 8-byte signature (RSA/HMAC) - verifyPassword check
//   3. N = 0xFF - seed[4] - A    (iteration count - 4 for E92 since seed[4]=06)
//   4. h = payload; for _ in 0..N-1: h = SHA-256(h)
//   5. aes_key = h[0..15]                          (first 16 bytes)
//   6. plaintext  = [0xFF * 11 || seed[0..4]]      (16 bytes, one AES block)
//   7. ciphertext = AES-128-ECB-Encrypt(aes_key, plaintext)
//   8. response_key = ciphertext[0..4]             (first 5 bytes)
//
// Verified test vectors (algoId=0x92, default password below):
//   11 22 33 44 06 -> EC BF F7 87 A4   (hook-captured ground truth)
//   43 89 30 D3 06 -> CD CF 83 5F 22
//   91 81 4E B9 06 -> B0 7B 1E 76 BC
//   C0 CA D2 9E 06 -> 1E 84 FD BC 03
//   DE F0 8C D3 06 -> F3 B2 B4 55 C8
//   9C 92 6F F5 06 -> 0D 16 59 D3 B9
//   D8 B1 D5 40 06 -> 23 B7 1F FC F4
// See Gm5ByteAlgorithmTests.
//
// Default Algo 92 password (captured from DPS 4.52.2000, applies to E92-family
// ECMs; same value for every E92 install of DPS we know of):
//   "01sgqbD6nsKDz8SawCanylLyqwtoFUeMsY2Y6FxEi4rP0A9QCSAP8Ivi0OzQk="
//
// Configuration (optional, via SecurityModuleConfig JsonElement):
//   {
//     "algoId":   "0x92",                               // required if not 0x92
//     "password": "01...zQk=",                           // 62-char ASCII
//     "familyByte": "0x06",                              // seed[4]; default 0x06
//     "fixedSeed": "1122334406"                          // 10 hex chars
//   }
// algoId / password / familyByte all default to the Algo 92 / E92 values.
public sealed class Gm5ByteAlgorithm : ISeedKeyAlgorithm
{
    // Algo 92 / E92-family default. Capture method documented above.
    public const string DefaultAlgo92Password =
        "01sgqbD6nsKDz8SawCanylLyqwtoFUeMsY2Y6FxEi4rP0A9QCSAP8Ivi0OzQk=";

    public const byte DefaultE92FamilyByte = 0x06;
    public const int  DefaultAlgoId       = 0x92;

    private byte[]? fixedSeed;
    private string  password  = DefaultAlgo92Password;
    private int     algoId    = DefaultAlgoId;
    private byte    familyByte = DefaultE92FamilyByte;

    public string Id => "gm-algo-92";
    public int SeedLength => 5;
    public int KeyLength  => 5;
    public IEnumerable<byte> SupportedLevels { get; } = new byte[] { 1 };
    public ProgrammingSessionBehavior ProgrammingSession => ProgrammingSessionBehavior.UnchangedAlgorithm;

    public void GenerateSeed(byte level, Span<byte> seedBuffer, out int seedLength)
    {
        if (fixedSeed is not null)
        {
            fixedSeed.AsSpan().CopyTo(seedBuffer);
        }
        else
        {
            Random.Shared.NextBytes(seedBuffer.Slice(0, 5));
            // DPS rejects seeds whose family byte (seed[4]) does not match the
            // algorithm's expected family. For E92 that byte is 0x06; force it.
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
        password   = DefaultAlgo92Password;
        algoId     = DefaultAlgoId;
        familyByte = DefaultE92FamilyByte;

        if (config is null || config.Value.ValueKind != JsonValueKind.Object) return;

        if (config.Value.TryGetProperty("algoId", out var pAlgo) &&
            pAlgo.ValueKind == JsonValueKind.String &&
            TryParseHexUInt(pAlgo.GetString(), out var aId))
            algoId = (int)aId;

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
    /// The bare math. Throws on malformed password or algoId mismatch. Useful
    /// for unit tests and brute-forcers; the production path goes through
    /// <see cref="ComputeExpectedKey"/> which catches and reports InvalidKey.
    /// </summary>
    public static void ComputeKey(ReadOnlySpan<byte> seed, int algoId, string password, Span<byte> outKey)
    {
        if (seed.Length != 5) throw new ArgumentException("seed must be 5 bytes", nameof(seed));
        if (outKey.Length < 5) throw new ArgumentException("outKey must be >= 5 bytes", nameof(outKey));
        if (password is null || password.Length != 62)
            throw new ArgumentException("password must be 62 ASCII chars (2 prefix + 60 base64)", nameof(password));

        // 1. Length marker
        if (!int.TryParse(password.AsSpan(0, 2), out var marker) || (marker != 1 && marker != 3))
            throw new InvalidOperationException("password length marker must be \"01\" or \"03\"");

        // 2. Base64-decode 60 chars -> 44+ bytes (last byte is padding-related).
        var blob = Convert.FromBase64String(password.Substring(2, 60));
        if (blob.Length < 44) throw new InvalidOperationException("password base64 decoded too short");

        // 3. payload + A + B + signature
        var payload = blob.AsSpan(0, 32);
        int A = (blob[32] << 8) | blob[33];
        int B = (blob[34] << 8) | blob[35];
        // blob[36..43] = 8-byte signature; verifyPassword would check this against
        // an embedded public key. We don't have the key, but if the password was
        // ever tampered with, the AES key derived below will be wrong and the
        // computed key won't match what DPS produced. Trust-on-first-capture.

        if (B != algoId)
            throw new InvalidOperationException(
                $"password's embedded algoId 0x{B:X4} != requested 0x{algoId:X4}");
        if (A > 0xFF - seed[4])
            throw new InvalidOperationException("A would underflow iteration count");

        int nIter = 0xFF - seed[4] - A;

        // 4. SHA-256 iterated N times on the 32-byte payload.
        Span<byte> h = stackalloc byte[32];
        payload.CopyTo(h);
        Span<byte> tmp = stackalloc byte[32];
        for (int i = 0; i < nIter; i++)
        {
            SHA256.HashData(h, tmp);
            tmp.CopyTo(h);
        }

        // 5. AES key = first 16 bytes
        // 6. plaintext = [FF*11 || seed]
        Span<byte> plaintext = stackalloc byte[16];
        plaintext.Slice(0, 11).Fill(0xFF);
        seed.CopyTo(plaintext.Slice(11));

        // 7. AES-128-ECB-Encrypt
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.KeySize = 128;
        aes.Key = h.Slice(0, 16).ToArray();
        Span<byte> ciphertext = stackalloc byte[16];
        aes.EncryptEcb(plaintext, ciphertext, PaddingMode.None);

        // 8. First 5 bytes of ciphertext = response key
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
