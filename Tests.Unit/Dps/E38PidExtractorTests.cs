using Core.Dps;
using Xunit;

namespace EcuSimulator.Tests.Dps;

public sealed class E38PidExtractorTests
{
    // Real 2 MiB E38 readback from the 2011 Silverado, referenced by
    // memory file reference_e38_pid_extraction.md (known table anchor 0x145718).
    private const string FixturePath =
        @"C:\Users\Nathan\.claude\projects\C--Users-Nathan-OneDrive-ECA-Resources-Visual-Studio-GM-ECU-Simulator\agent_e38_extract\BINARY READ.bin";

    [Fact]
    public void Extract_RealE38Bin_FindsKnownTable()
    {
        Assert.True(File.Exists(FixturePath), $"Fixture missing: {FixturePath}");

        var (records, offset) = E38PidExtractor.ExtractFile(FixturePath);

        Assert.Equal(0x145718, offset);
        Assert.InRange(records.Count, 530, 540);
        // First-record type varies between bins (observed 0x04 on the
        // 2011 Silverado readback) - just assert it is one of the valid
        // type bytes the scanner accepts.
        Assert.Contains(records[0].Type, E38PidExtractor.ValidTypes);

        ushort prev = 0;
        foreach (var rec in records)
        {
            Assert.True(rec.Pid > prev, $"PIDs not strictly increasing at {rec.Pid:X4} after {prev:X4}");
            prev = rec.Pid;
            Assert.InRange(rec.Size, E38PidExtractor.MinSize, E38PidExtractor.MaxSize);
        }
    }

    [Fact]
    public void Extract_AllOnesBlob_Throws()
    {
        byte[] blob = new byte[1024];
        Array.Fill(blob, (byte)0xFF);

        Assert.Throws<InvalidDataException>(() => E38PidExtractor.Extract(blob));
    }

    [Fact]
    public void Extract_SynthesizedTable_FindsExactly200Records()
    {
        const int count = 200;
        const int pad = 256;
        byte[] blob = new byte[pad + count * E38PidExtractor.RecordSize + pad];
        Array.Fill(blob, (byte)0xFF);

        for (int i = 0; i < count; i++)
        {
            int off = pad + i * E38PidExtractor.RecordSize;
            ushort pid = (ushort)(i + 1);
            ushort size = (ushort)((i % 200) + 1);
            blob[off + 0] = 0x01;
            blob[off + 1] = 0x00;
            blob[off + 2] = (byte)(pid >> 8);
            blob[off + 3] = (byte)(pid & 0xFF);
            blob[off + 4] = (byte)(size >> 8);
            blob[off + 5] = (byte)(size & 0xFF);
            blob[off + 6] = 0xAB;
            blob[off + 7] = 0xCD;
        }

        var (records, offset) = E38PidExtractor.Extract(blob);

        Assert.Equal(pad, offset);
        Assert.Equal(count, records.Count);
        Assert.Equal((ushort)1, records[0].Pid);
        Assert.Equal((ushort)count, records[^1].Pid);
        Assert.All(records, r => Assert.Equal((byte)0x01, r.Type));
    }
}
