using System.Globalization;
using System.Text.Json;

namespace Core.Security.Algorithms;

// Cipher used by the gm-bypass-{N}byte registry entries. Has no real key
// math: GenerateSeed returns an N-byte random seed (or a fixed seed via
// LoadConfig); ComputeExpectedKey always returns false. Both methods are
// only reached when the wrapping Gmw3110_2010_Generic is configured with
// SecurityModuleBehaviour.Strict - bypass mode short-circuits before
// either runs, so the false return from ComputeExpectedKey is never
// observed in normal use.
//
// Why a real cipher class rather than a marker: the generic module reads
// SeedLength to size its seed buffer and decide how many zero bytes to
// emit in the BypassAll seed response. A 5-byte bypass module must report
// SeedLength=5 or DPS rejects with "ECU did not return a 5-byte seed".
//
// Configuration (optional, via SecurityModuleConfig JsonElement):
//     { "fixedSeed": "1122334406" }   // hex, exactly 2*SeedLength chars
// Allows repeatable test scenarios; absent -> Random.Shared.NextBytes.
public sealed class RandomSeedCipher : ISeedKeyAlgorithm
{
    private readonly int seedLength;
    private byte[]? fixedSeed;

    public RandomSeedCipher(int seedLength)
    {
        if (seedLength < 1 || seedLength > 32)
            throw new ArgumentOutOfRangeException(nameof(seedLength), "Seed length must be 1..32 bytes");
        this.seedLength = seedLength;
    }

    public string Id => $"random-seed-{seedLength}byte";
    public int SeedLength => seedLength;
    public int KeyLength  => seedLength;
    public IEnumerable<byte> SupportedLevels { get; } = new byte[] { 1 };

    public void GenerateSeed(byte level, Span<byte> seedBuffer, out int seedLength)
    {
        if (fixedSeed is not null)
        {
            fixedSeed.AsSpan().CopyTo(seedBuffer);
        }
        else
        {
            Random.Shared.NextBytes(seedBuffer.Slice(0, this.seedLength));
            // Avoid an all-zero seed - the generic module's IsUnlocked path
            // treats all-zero as "already unlocked", which would short-circuit
            // a freshly-locked ECU. Flip a byte to guarantee non-zero.
            bool allZero = true;
            for (int i = 0; i < this.seedLength; i++)
            {
                if (seedBuffer[i] != 0) { allZero = false; break; }
            }
            if (allZero) seedBuffer[0] = 1;
        }
        seedLength = this.seedLength;
    }

    public bool ComputeExpectedKey(byte level, ReadOnlySpan<byte> seed, Span<byte> keyBuffer, out int keyLength)
    {
        // Bypass modules never reach this path. If a misconfigured caller
        // wraps RandomSeedCipher in a Strict module, fail closed: NRC $35.
        keyLength = 0;
        return false;
    }

    public void LoadConfig(JsonElement? config)
    {
        fixedSeed = null;
        if (config is null || config.Value.ValueKind != JsonValueKind.Object) return;
        if (!config.Value.TryGetProperty("fixedSeed", out var prop)) return;
        if (prop.ValueKind != JsonValueKind.String) return;
        var hex = prop.GetString();
        if (string.IsNullOrWhiteSpace(hex)) return;
        var s = hex!.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        // Strip whitespace so "11 22 33 44 06" works the same as "1122334406".
        if (s.IndexOfAny(new[] { ' ', '\t' }) >= 0)
            s = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (s.Length != seedLength * 2) return;
        var bytes = new byte[seedLength];
        for (int i = 0; i < seedLength; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2),
                               NumberStyles.HexNumber,
                               CultureInfo.InvariantCulture,
                               out bytes[i])) return;
        }
        fixedSeed = bytes;
    }
}
