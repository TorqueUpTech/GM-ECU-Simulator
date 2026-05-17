using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Core.Dps;

// Extracts and indexes a GM DPS / SPAT programming archive. A DPS .zip contains
// one 5206-byte .tbl manifest, one utility-file .bin (SPS interpreter bytecode),
// and N calibration-file .bin payloads. The .tbl pairs each filename with a
// label - "Utility File" for the bytecode, "Description of Cal" for cal images -
// at fixed offsets in an otherwise null-padded record. See the project memory
// file reference_dps_utility_file_format.md for the format and the SPAT
// reverse-engineering history.
public sealed record DpsArchiveEntry(string FileName, DpsArchiveEntryKind Kind);

public enum DpsArchiveEntryKind
{
    UtilityFile,
    Calibration,
}

public sealed class DpsArchive : IDisposable
{
    private bool _disposed;

    internal DpsArchive(
        string archivePath,
        string extractDir,
        IReadOnlyList<DpsArchiveEntry> manifest,
        string utilityFilePath,
        IReadOnlyList<string> calibrationFilePaths,
        string? osCalFilePath)
    {
        ArchivePath = archivePath;
        ExtractDir = extractDir;
        Manifest = manifest;
        UtilityFilePath = utilityFilePath;
        CalibrationFilePaths = calibrationFilePaths;
        OsCalFilePath = osCalFilePath;
    }

    // Source .zip path supplied to ArchiveExtractor.Extract.
    public string ArchivePath { get; }

    // Temp directory holding the extracted entries; removed on Dispose.
    public string ExtractDir { get; }

    // Manifest in encounter order. Index 0 is always the utility file; the rest
    // are calibration files in the order recorded by SPAT.
    public IReadOnlyList<DpsArchiveEntry> Manifest { get; }

    // Absolute path to the utility-file bytecode under ExtractDir.
    public string UtilityFilePath { get; }

    // Absolute paths to the calibration bins, in manifest order.
    public IReadOnlyList<string> CalibrationFilePaths { get; }

    // The largest calibration by size, treated as the OS module. On E38 archives
    // the OS image (~1.7 MB) is always the largest by a wide margin, so a size
    // heuristic is reliable. If multiple cals are within 10% of the maximum
    // size, the first one in manifest order is returned. Null if no cals.
    public string? OsCalFilePath { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            if (Directory.Exists(ExtractDir))
            {
                Directory.Delete(ExtractDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup. The temp dir might already be gone or a
            // file inside might still be held open by an antivirus scanner;
            // either way it's not worth surfacing.
        }
    }
}

public static class ArchiveExtractor
{
    // Filename pattern observed in real DPS archives: 7-9 ASCII digits followed
    // by ".bin". The leading and trailing word boundaries keep us from matching
    // longer alphanumeric identifiers that happen to contain a run of digits.
    private static readonly Regex _binFileNameRegex = new(
        @"\b\d{7,9}\.bin\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Window after each filename in which the label is expected to live.
    // Empirically the label sits ~38 bytes after the filename in a null-padded
    // 96-byte record; 200 bytes is generous without risking the next record.
    private const int LabelSearchWindowBytes = 200;

    private const string UtilityLabel = "Utility File";
    private const string CalibrationLabel = "Description of Cal";

    public static DpsArchive Extract(string archiveZipPath)
    {
        if (string.IsNullOrWhiteSpace(archiveZipPath))
        {
            throw new ArgumentException("Archive path is required.", nameof(archiveZipPath));
        }
        if (!File.Exists(archiveZipPath))
        {
            throw new FileNotFoundException("DPS archive not found.", archiveZipPath);
        }

        string extractDir = Path.Combine(
            Path.GetTempPath(),
            "GmEcuSim.Prime." + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(extractDir);

        try
        {
            ZipFile.ExtractToDirectory(archiveZipPath, extractDir);

            string tblPath = FindManifestFile(extractDir)
                ?? throw new InvalidDataException(
                    $"DPS archive manifest not found: no .tbl file inside '{archiveZipPath}'.");

            byte[] tblBytes = File.ReadAllBytes(tblPath);
            var manifest = ParseManifest(tblBytes);

            var utilityEntries = manifest.Where(e => e.Kind == DpsArchiveEntryKind.UtilityFile).ToList();
            if (utilityEntries.Count == 0)
            {
                throw new InvalidDataException(
                    $"DPS archive manifest declares no utility file: '{tblPath}'.");
            }
            if (utilityEntries.Count > 1)
            {
                throw new InvalidDataException(
                    $"DPS archive manifest declares {utilityEntries.Count} utility files; expected exactly one.");
            }

            // The utility-file entry is conventionally the first record; surface
            // that ordering in the public Manifest as well.
            var ordered = new List<DpsArchiveEntry>(manifest.Count) { utilityEntries[0] };
            foreach (var entry in manifest)
            {
                if (!ReferenceEquals(entry, utilityEntries[0]))
                {
                    ordered.Add(entry);
                }
            }

            string utilityFilePath = Path.Combine(extractDir, utilityEntries[0].FileName);
            var calibrationFilePaths = ordered
                .Where(e => e.Kind == DpsArchiveEntryKind.Calibration)
                .Select(e => Path.Combine(extractDir, e.FileName))
                .ToList();

            string? osCalFilePath = PickOsCalibration(calibrationFilePaths);

            return new DpsArchive(
                archivePath: archiveZipPath,
                extractDir: extractDir,
                manifest: ordered,
                utilityFilePath: utilityFilePath,
                calibrationFilePaths: calibrationFilePaths,
                osCalFilePath: osCalFilePath);
        }
        catch
        {
            // Don't leave a half-populated temp dir behind on failure.
            try
            {
                if (Directory.Exists(extractDir))
                {
                    Directory.Delete(extractDir, recursive: true);
                }
            }
            catch
            {
                // Suppressed: the original exception is the interesting one.
            }
            throw;
        }
    }

    private static string? FindManifestFile(string extractDir)
    {
        // Case-insensitive search; SPAT consistently emits lowercase .tbl but
        // we don't want to rely on filesystem casing.
        return Directory
            .EnumerateFiles(extractDir, "*.*", SearchOption.AllDirectories)
            .FirstOrDefault(p => string.Equals(
                Path.GetExtension(p),
                ".tbl",
                StringComparison.OrdinalIgnoreCase));
    }

    private static List<DpsArchiveEntry> ParseManifest(byte[] tblBytes)
    {
        // ASCII decode is safe here: every observed .tbl uses 7-bit ASCII for
        // filenames and labels, with null padding everywhere else.
        string text = Encoding.ASCII.GetString(tblBytes);
        var entries = new List<DpsArchiveEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in _binFileNameRegex.Matches(text))
        {
            string fileName = match.Value;
            // Defensive against (theoretical) duplicate filename mentions in
            // the manifest body; keep only the first occurrence.
            if (!seen.Add(fileName))
            {
                continue;
            }

            int windowStart = match.Index + match.Length;
            int windowLength = Math.Min(LabelSearchWindowBytes, text.Length - windowStart);
            string window = text.Substring(windowStart, windowLength);

            DpsArchiveEntryKind kind;
            // Order matters: "Description of Cal" doesn't contain "Utility
            // File", but we still check the utility label first so a stray
            // future label that happens to share a prefix doesn't shadow it.
            if (ContainsLabel(window, UtilityLabel))
            {
                kind = DpsArchiveEntryKind.UtilityFile;
            }
            else if (ContainsLabel(window, CalibrationLabel))
            {
                kind = DpsArchiveEntryKind.Calibration;
            }
            else
            {
                // Filename present but no label - skip rather than mislabel.
                // The .tbl template area near the top of the file contains
                // example placeholders that would otherwise be mis-classified.
                continue;
            }

            entries.Add(new DpsArchiveEntry(fileName, kind));
        }

        return entries;
    }

    private static bool ContainsLabel(string window, string label)
    {
        // The manifest sandwiches the label between null bytes, which survive
        // ASCII-decoding as '\0'. IndexOf with Ordinal handles that cleanly.
        return window.IndexOf(label, StringComparison.Ordinal) >= 0;
    }

    private static string? PickOsCalibration(IReadOnlyList<string> calibrationFilePaths)
    {
        if (calibrationFilePaths.Count == 0)
        {
            return null;
        }

        long maxSize = -1;
        foreach (var path in calibrationFilePaths)
        {
            long size = new FileInfo(path).Length;
            if (size > maxSize)
            {
                maxSize = size;
            }
        }

        // 10% tolerance: if a second cal is suspiciously close to the largest,
        // fall back to manifest order rather than guess which one is the OS.
        long threshold = (long)(maxSize * 0.9);
        foreach (var path in calibrationFilePaths)
        {
            if (new FileInfo(path).Length >= threshold)
            {
                return path;
            }
        }

        return calibrationFilePaths[0];
    }
}
