using System.IO;
using System.IO.Compression;
using Core.Dps;
using Xunit;

namespace EcuSimulator.Tests.Dps;

public sealed class ArchiveExtractorTests
{
    // Real GM SPAT-built DPS archive used during the 2026-05-16 end-to-end DPS
    // programming validation (project_dps_e2e_validated.md). Contains one
    // utility file plus eight calibration files; the OS module is the 1.77 MB
    // 12639835.bin.
    private const string ReferenceArchive = @"C:\DpsArch\E38_1GCRKSE36BZ158034.zip";

    private const string ExpectedUtilityFile = "12645553.bin";
    private const string ExpectedOsCalFile = "12639835.bin";

    private static readonly string[] ExpectedCalsInOrder =
    {
        "12639835.bin",
        "12637641.bin",
        "12636563.bin",
        "12620806.bin",
        "12656714.bin",
        "12656698.bin",
        "12625892.bin",
        "12620683.bin",
    };

    [Fact]
    public void Extract_RealE38Archive_ParsesManifestAndResolvesPaths()
    {
        Assert.True(File.Exists(ReferenceArchive), $"Fixture missing: {ReferenceArchive}");

        string extractDirCaptured;
        using (var archive = ArchiveExtractor.Extract(ReferenceArchive))
        {
            extractDirCaptured = archive.ExtractDir;

            Assert.True(Directory.Exists(archive.ExtractDir),
                $"Extract dir should exist while archive is live: {archive.ExtractDir}");
            Assert.Equal(ReferenceArchive, archive.ArchivePath);

            // One utility file + eight cal files.
            Assert.Equal(9, archive.Manifest.Count);

            Assert.Equal(DpsArchiveEntryKind.UtilityFile, archive.Manifest[0].Kind);
            Assert.Equal(ExpectedUtilityFile, archive.Manifest[0].FileName);

            for (int i = 0; i < ExpectedCalsInOrder.Length; i++)
            {
                var entry = archive.Manifest[i + 1];
                Assert.Equal(DpsArchiveEntryKind.Calibration, entry.Kind);
                Assert.Equal(ExpectedCalsInOrder[i], entry.FileName);
            }

            Assert.EndsWith(ExpectedUtilityFile, archive.UtilityFilePath);
            Assert.True(File.Exists(archive.UtilityFilePath),
                $"Utility file should be extracted to disk: {archive.UtilityFilePath}");

            Assert.Equal(8, archive.CalibrationFilePaths.Count);
            for (int i = 0; i < ExpectedCalsInOrder.Length; i++)
            {
                string calPath = archive.CalibrationFilePaths[i];
                Assert.EndsWith(ExpectedCalsInOrder[i], calPath);
                Assert.True(File.Exists(calPath),
                    $"Calibration file should be on disk: {calPath}");
            }

            Assert.NotNull(archive.OsCalFilePath);
            Assert.EndsWith(ExpectedOsCalFile, archive.OsCalFilePath!);
        }

        Assert.False(Directory.Exists(extractDirCaptured),
            $"Extract dir should be cleaned up after Dispose: {extractDirCaptured}");
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        Assert.True(File.Exists(ReferenceArchive), $"Fixture missing: {ReferenceArchive}");

        var archive = ArchiveExtractor.Extract(ReferenceArchive);
        archive.Dispose();
        archive.Dispose();
    }

    [Fact]
    public void Extract_NonZipFile_Throws()
    {
        // Write random bytes to a .zip-named scratch file so ExtractToDirectory
        // is the layer that rejects it (rather than path validation).
        string scratch = Path.Combine(Path.GetTempPath(), $"GmEcuSim.NotAZip.{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(scratch, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 });
        try
        {
            Assert.ThrowsAny<Exception>(() => ArchiveExtractor.Extract(scratch));
        }
        finally
        {
            try { File.Delete(scratch); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Extract_ZipWithoutTbl_ThrowsInvalidDataWithManifestMessage()
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"GmEcuSim.NoTbl.{Guid.NewGuid():N}.zip");
        using (var fs = File.Create(scratch))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("12345678.bin");
            using var entryStream = entry.Open();
            entryStream.Write(new byte[] { 0x01, 0x02, 0x03, 0x04 }, 0, 4);
        }

        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => ArchiveExtractor.Extract(scratch));
            Assert.Contains("manifest not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(scratch); } catch { /* best effort */ }
        }
    }
}
