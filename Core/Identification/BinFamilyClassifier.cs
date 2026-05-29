using System.Text;

namespace Core.Identification;

// Classifies a GM ECU flash readback into one of the families we know how
// to extract identity from (T43 TCM, E38 PCM, E67 PCM). Lifted out of
// Mode1ADidBinExtractor so the bin-add flow can reject unrecognised files
// up-front - before attempting the expensive dispatcher walk - and the
// rejection dialog can name what's supported in one clear place.
//
// The signatures are the same ones the dispatcher walker used internally:
//   - T43:   "BOSCH TC19.12" ASCII marker near 0x1FFA0.
//   - E67:   25-byte VIN descriptor at 0xC0AC or 0xE0AC AND a "BOSCH"
//            marker AND no "DELPHI" marker.
//   - E38:   25-byte VIN descriptor at 0xC0AC or 0xE0AC. No supplier
//            ASCII required; Continental-supplied 2010+ trucks carry
//            neither BOSCH nor DELPHI.
public static class BinFamilyClassifier
{
    public enum Family { Unknown, T43, E38, E67 }

    /// <summary>
    /// String form of <see cref="Family"/> as used by <see cref="GmFamilyDefinitions"/>
    /// and the JSON config schema. Matches the strings the legacy
    /// <c>Mode1ADidBinExtractor.DetectFamily</c> returned so downstream
    /// consumers don't need to change.
    /// </summary>
    public static string Name(Family family) => family switch
    {
        Family.T43 => "T43",
        Family.E38 => "E38",
        Family.E67 => "E67",
        _          => "Unknown",
    };

    /// <summary>
    /// Identify the family of a flash image. Cheap - one ASCII scan plus
    /// two fixed-offset descriptor checks; safe to call before the full
    /// dispatcher walk in <see cref="Mode1ADidBinExtractor.Parse"/>.
    /// </summary>
    public static Family Classify(ReadOnlySpan<byte> bin)
    {
        // T43 has a distinctive "BOSCH TC19.12" marker near 0x1FFA0 (end of
        // the Bosch project header). Strongest single signature we have.
        if (FindAscii(bin, "BOSCH TC19.12") >= 0) return Family.T43;
        // E38 vs E67 distinguishable by VIN-descriptor block location and
        // supplier marker presence.
        if (LooksLikeE67(bin)) return Family.E67;
        if (LooksLikeE38(bin)) return Family.E38;
        return Family.Unknown;
    }

    private static bool LooksLikeE67(ReadOnlySpan<byte> d)
    {
        // E67 (Bosch ME9-based): VIN descriptor at either 0xC0AC (2010+) or
        // 0xE0AC (some 2009-era bins). Require a Bosch ASCII marker to
        // confirm - the previous "VIN@0xC0AC unconditionally = E67" rule
        // mis-detected Continental-supplied E38 bins (e.g. 2011 Silverado
        // 6.0L LY6) that also live at 0xC0AC but aren't Bosch ME9.
        bool hasVin = HasAsciiVinDescriptor(d, 0xC0AC)
                      || HasAsciiVinDescriptor(d, 0xE0AC);
        if (!hasVin) return false;
        if (FindAscii(d, "DELPHI") >= 0) return false;
        return FindAscii(d, "BOSCH") >= 0;
    }

    private static bool LooksLikeE38(ReadOnlySpan<byte> d)
    {
        // E38 (Delphi-supplied 2008-ish era, Continental on 2010+ trucks):
        // VIN block lives at 0xE0AC on older Delphi bins and 0xC0AC on the
        // 2010+ memory map. No supplier ASCII marker required - Continental-
        // supplied 6.0L Silverado bins carry no `BOSCH`/`DELPHI` string at all,
        // so the prior "BOSCH must be absent" check was redundant with E67's
        // positive Bosch-marker requirement: if no Bosch marker is present
        // AND a VIN descriptor lands at one of the two known offsets, this
        // is E38.
        return HasAsciiVinDescriptor(d, 0xC0AC)
               || HasAsciiVinDescriptor(d, 0xE0AC);
    }

    private static bool HasAsciiVinDescriptor(ReadOnlySpan<byte> d, int off)
    {
        // The descriptor block has the form `<8-char tail><17-char VIN>`. We
        // only need to confirm 25 printable ASCII bytes here; full VIN
        // validation happens in Mode1ADidBinExtractor.ExtractFlashMetadata.
        if (off < 0 || off + 25 > d.Length) return false;
        for (int i = 0; i < 25; i++)
        {
            byte b = d[off + i];
            if (b < 0x20 || b > 0x7E) return false;
        }
        return true;
    }

    private static int FindAscii(ReadOnlySpan<byte> d, string s)
    {
        Span<byte> needle = stackalloc byte[s.Length];
        Encoding.ASCII.GetBytes(s, needle);
        for (int i = 0; i + needle.Length <= d.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (d[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}
