using System.Globalization;
using System.IO;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// Writes captured $36 transfer data to disk. Every $36 produces one .bin
// per dataRecord (WriteEachTransferData), and EcuExitLogic dumps one .bin
// per declared erase region at session end (WriteFlashRegions).
//
// Writes always happen when bus.Capture.CaptureDirectory is set; they no-op
// when it is null. WPF startup sets the directory; unit tests leave it null
// so they don't pollute the user's real captures folder.
public static class BootloaderCaptureWriter
{
    /// <summary>
    /// Per-$36 immediate write. Each $36 TransferData's dataRecord is dumped
    /// to its own .bin file - no reassembly buffer, no sparse-image offset
    /// math, no $34-bracket merging. Filename embeds a session-scoped seq
    /// (D3 since a programming session can issue dozens of $36s), the $36's
    /// startingAddress, and the dataRecord length.
    ///
    /// Result: a flash-tool author scanning the captures dir sees one file
    /// per logical "push" - the kernel pieces are obvious by their distinct
    /// sizes/addresses, staging-buffer cal chunks are obvious by repetition.
    /// </summary>
    public static void WriteEachTransferData(EcuNode node, VirtualBus bus,
                                             uint startingAddress, ReadOnlySpan<byte> dataRecord)
    {
        var settings = bus.Capture;
        if (string.IsNullOrEmpty(settings.CaptureDirectory)) return;
        if (dataRecord.Length == 0) return;

        try
        {
            // Pin the session timestamp on the first capture write so every
            // .bin from this session shares a stable yyyymmdd_HHmmss prefix.
            if (node.State.DownloadCaptureSessionTimestampUtc is null)
                node.State.DownloadCaptureSessionTimestampUtc = DateTime.UtcNow;

            var tsUtc = node.State.DownloadCaptureSessionTimestampUtc.Value;
            uint seq = node.State.DownloadCaptureSequence;
            string ts = tsUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            // Per-session subdirectory so a fresh download doesn't fill the
            // captures root with dozens of loose files. Subdir name pins the
            // ECU + session start; filename inside is just seq + addr + len.
            string sessionDir = string.Format(CultureInfo.InvariantCulture,
                "{0}_{1}", Sanitise(node.Name), ts);
            string fullDir = Path.Combine(settings.CaptureDirectory!, sessionDir);
            Directory.CreateDirectory(fullDir);

            string fileName = string.Format(CultureInfo.InvariantCulture,
                "{0:D3}_{1:X8}_{2}.bin", seq, startingAddress, dataRecord.Length);
            string path = Path.Combine(fullDir, fileName);

            File.WriteAllBytes(path, dataRecord.ToArray());
            node.State.DownloadCaptureSequence++;

            bus.LogSim?.Invoke($"[capture] $36 #{seq}: {dataRecord.Length} B @ 0x{startingAddress:X8} -> {path}");
            settings.RaiseCaptureWritten(path);
        }
        catch (Exception ex)
        {
            bus.LogSim?.Invoke($"[capture] write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Session-end consolidated flash dump. For every region the kernel
    /// declared via $31 EraseMemoryByAddress, writes one .bin sized to the
    /// declared erase length (any byte not overwritten by a $36 stays at
    /// the post-erase $FF, matching the on-device reality). Files land in
    /// the same per-session subdirectory as the per-$36 fragments so a
    /// user can compare the contiguous image against the individual pieces.
    ///
    /// Naming: {seq:D3}_flash_{start:X8}_{size}.bin. The leading seq comes
    /// from DownloadCaptureSequence so the flash dump sorts after the
    /// individual $36 fragments that built it.
    ///
    /// Called from EcuExitLogic before ClearProgrammingState wipes the
    /// regions. No-op when no CaptureDirectory is set or no region was
    /// declared.
    /// </summary>
    public static void WriteFlashRegions(EcuNode node, VirtualBus bus)
    {
        var settings = bus.Capture;
        if (string.IsNullOrEmpty(settings.CaptureDirectory)) return;
        if (node.State.CapturedFlashRegions.Count == 0) return;

        try
        {
            var tsUtc = node.State.DownloadCaptureSessionTimestampUtc ?? DateTime.UtcNow;
            string ts = tsUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string sessionDir = string.Format(CultureInfo.InvariantCulture,
                "{0}_{1}", Sanitise(node.Name), ts);
            string fullDir = Path.Combine(settings.CaptureDirectory!, sessionDir);
            Directory.CreateDirectory(fullDir);

            foreach (var region in node.State.CapturedFlashRegions)
            {
                uint seq = node.State.DownloadCaptureSequence++;
                string fileName = string.Format(CultureInfo.InvariantCulture,
                    "{0:D3}_flash_{1:X8}_{2}.bin", seq, region.StartAddress, region.Size);
                string path = Path.Combine(fullDir, fileName);

                File.WriteAllBytes(path, region.Buffer);

                bus.LogSim?.Invoke(
                    $"[capture] flash region 0x{region.StartAddress:X8} +{region.Size} " +
                    $"({region.BytesWritten} B written, rest 0xFF) -> {path}");
                settings.RaiseCaptureWritten(path);
            }
        }
        catch (Exception ex)
        {
            bus.LogSim?.Invoke($"[capture] flash region write failed: {ex.Message}");
        }
    }

    private static string Sanitise(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buf = new char[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            buf[i] = Array.IndexOf(invalid, raw[i]) >= 0 ? '_' : raw[i];
        return new string(buf);
    }
}
