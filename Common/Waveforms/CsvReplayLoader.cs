using System.Globalization;

namespace Common.Waveforms;

// Parses a per-PID waveform CSV. Column A is the time stamp (seconds),
// column B is the engineering-unit value; any further columns are ignored.
// A non-numeric first row is treated as a header and skipped silently.
public static class CsvReplayLoader
{
    public sealed record LoadResult(
        IReadOnlyList<(double TimeSec, double Value)> Samples,
        bool SkippedHeader);

    /// <summary>
    /// Validates and parses <paramref name="path"/>. Throws
    /// <see cref="FormatException"/> with a row-numbered message when the file
    /// fails the contract (non-numeric data row, fewer than 2 columns, time
    /// not strictly increasing, empty).
    /// </summary>
    public static LoadResult Load(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
            throw new FormatException("CSV file is empty.");

        var samples = new List<(double, double)>(lines.Length);
        bool skippedHeader = false;
        double lastT = double.NegativeInfinity;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(',');
            if (parts.Length < 2)
                throw new FormatException($"Row {i + 1}: expected at least 2 comma-separated columns, got {parts.Length}.");

            bool parsedT = double.TryParse(parts[0].Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var t);
            bool parsedV = double.TryParse(parts[1].Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var v);

            if (!parsedT || !parsedV)
            {
                // One leading non-numeric row is tolerated as a header.
                if (samples.Count == 0 && !skippedHeader) { skippedHeader = true; continue; }
                throw new FormatException(
                    $"Row {i + 1}: columns A and B must be numeric (got '{parts[0]}', '{parts[1]}').");
            }

            if (t <= lastT)
                throw new FormatException(
                    $"Row {i + 1}: time column must be strictly increasing (got {t} after {lastT}).");

            samples.Add((t, v));
            lastT = t;
        }

        if (samples.Count == 0)
            throw new FormatException("CSV file contains no numeric data rows.");

        return new LoadResult(samples, skippedHeader);
    }
}
