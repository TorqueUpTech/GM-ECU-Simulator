using System.Globalization;
using System.Text.Json;

namespace Core.Security.Algorithms;

// GM E67 ECM seed-key algorithm (GMLAN algorithm 0x89). Two-byte seed,
// two-byte key, level 1 only.
//
// Algorithm source: native function `KeyAlgoGm_$89` extracted from
// Daniel2345's PowerPCM_Flasher_0006.exe (RVA 0x6670, .text section of the
// C++/CLI mixed-mode assembly). The drop-down in PowerPCM_Flasher lists the
// E38 entry as "E38 - $92" and the E67 entry as "E67 - $89", and the two
// native cipher bodies are demonstrably different (brute-force check over
// all 65536 seeds returns zero collisions).
//
// Disassembly (cleaned up):
//     v = rol16(seed, 6)
//     v = bswap16(v)           ; asm helper returns up to 24 bits; only low 16
//                              ; survive the eventual & 0xFFFF in ror16, so
//                              ; the upper byte is harmless here
//     v = v - 0x55E9           ; 32-bit subtract
//     v = ror16(v & 0xFFFF, 2)
//     key = (v + 0x2A8E) & 0xFFFF
//
// Verified test vectors (computed from the disassembly above; arithmetic
// is unit-tested in E67AlgorithmTests):
//     0x0000 -> 0x1513
//     0x1234 -> 0x5637
//     0xABCD -> 0xAFD0
//     0xDEAD -> 0xB2FE
//     0xCAFE -> 0xC1C3
//     0xFFFF -> 0xD513
// Real captured pairs from hardware should be added there as additional
// Theory inputs when available.
//
// Programming-session policy: UnchangedAlgorithm. Same reasoning as E38 -
// the boot block runs the same $27 stub as the OS, and PowerPCM_Flasher
// uses this single algorithm regardless of session state.
//
// Configuration (optional, via SecurityModuleConfig JsonElement):
//     { "fixedSeed": "1234" }   // hex, 4 chars - locks the seed for repeatable testing
// No fixedSeed -> Random.Shared.NextBytes per request (more realistic).
public sealed class E67Algorithm : ISeedKeyAlgorithm
{
    private byte[]? fixedSeed;

    public string Id => "gm-e67-2byte";
    public int SeedLength => 2;
    public int KeyLength => 2;
    public IEnumerable<byte> SupportedLevels { get; } = new byte[] { 1 };

    public void GenerateSeed(byte level, Span<byte> seedBuffer, out int seedLength)
    {
        if (fixedSeed is not null)
        {
            fixedSeed.AsSpan().CopyTo(seedBuffer);
        }
        else
        {
            Random.Shared.NextBytes(seedBuffer.Slice(0, 2));
            if (seedBuffer[0] == 0 && seedBuffer[1] == 0) seedBuffer[0] = 1;
        }
        seedLength = 2;
    }

    public bool ComputeExpectedKey(byte level, ReadOnlySpan<byte> seed, Span<byte> keyBuffer, out int keyLength)
    {
        if (level != 1 || seed.Length != 2)
        {
            keyLength = 0;
            return false;
        }
        ushort s = (ushort)((seed[0] << 8) | seed[1]);
        ushort k = ComputeKey(s);
        keyBuffer[0] = (byte)(k >> 8);
        keyBuffer[1] = (byte)(k & 0xFF);
        keyLength = 2;
        return true;
    }

    public void LoadConfig(JsonElement? config)
    {
        fixedSeed = null;
        if (config is null || config.Value.ValueKind != JsonValueKind.Object) return;
        if (!config.Value.TryGetProperty("fixedSeed", out var prop)) return;
        if (prop.ValueKind != JsonValueKind.String) return;
        if (TryParseHex16(prop.GetString(), out var hi, out var lo))
            fixedSeed = new[] { hi, lo };
    }

    /// <summary>The bare math. Exposed for unit tests and brute-forcers.</summary>
    public static ushort ComputeKey(ushort seed)
    {
        uint v = Rol16(seed, 6);
        v = ((v & 0xFFFF) << 8) | ((v & 0xFFFF) >> 8);
        v = (v - 0x55E9) & 0xFFFFFFFF;
        v = Ror16((ushort)(v & 0xFFFF), 2);
        return (ushort)((v + 0x2A8E) & 0xFFFF);
    }

    private static uint Rol16(ushort x, int n)
    {
        n &= 15;
        return n == 0 ? x : (uint)(((x << n) | (x >> (16 - n))) & 0xFFFF);
    }

    private static uint Ror16(ushort x, int n)
    {
        n &= 15;
        return n == 0 ? x : (uint)(((x >> n) | (x << (16 - n))) & 0xFFFF);
    }

    private static bool TryParseHex16(string? hex, out byte hi, out byte lo)
    {
        hi = 0; lo = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex!.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length != 4) return false;
        return byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hi)
            && byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lo);
    }
}
