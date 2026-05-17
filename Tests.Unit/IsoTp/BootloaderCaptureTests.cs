using System.IO;
using Common.IsoTp;
using Common.PassThru;
using Core.Bus;
using Core.Ecu;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Shim.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// Coverage for the always-on bootloader-capture path. Service36Handler
// anchors on the first $36's address (so real-DPS absolute-address sessions
// work without any toggle); BootloaderCaptureWriter dumps each $36 to disk
// whenever CaptureSettings.CaptureDirectory is set. Tests that don't set
// the directory verify the silent no-op path (so unit-test runs don't
// pollute the user's real captures folder).
public class BootloaderCaptureTests
{
    private const ushort PhysReq = NodeFactory.PhysReq;
    private const ushort UsdtResp = NodeFactory.UsdtResp;

    private static (VirtualBus bus, EcuNode node, ChannelSession ch, Iso15765Channel iso)
        Wire()
    {
        var bus = new VirtualBus();
        var algo = new FakeSeedKeyAlgorithm();
        var node = NodeFactory.CreateNodeWithGenericModule(algo);
        // Test payloads use a 3-byte $36 starting address (0x003FB8).
        // Override the 4-byte default so the existing fixture data stays valid;
        // T43_4ByteAddressTests covers the 4-byte path explicitly.
        node.State.DownloadAddressByteCount = 3;
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.ISO15765, Baud = 500_000, Bus = bus };
        var iso = new Iso15765Channel(new IsoTpTimingParameters());
        iso.BusEgress = frame => bus.DispatchHostTx(frame, ch);
        ch.IsoChannel = iso;
        ch.IsoChannelInbound = (canId, frame) => iso.OnInboundCanFrame(canId, frame.AsSpan(4));
        iso.AddFilter(new Iso15765Channel.IsoFilter
        {
            Id = 1,
            MaskCanId = 0xFFFFFFFF,
            PatternCanId = UsdtResp,
            FlowCtlCanId = PhysReq,
            Format = AddressFormat.Normal,
        });
        return (bus, node, ch, iso);
    }

    private static byte[] Send(Iso15765Channel iso, byte[] req)
    {
        var begin = iso.BeginTransmit(PhysReq, req);
        Assert.True(begin.Started);
        iso.BusEgress!(begin.CanFrame!);
        iso.EndTransmit(begin.Filter!);
        Assert.True(iso.ReassembledPayloadQueue.TryDequeue(out var msg));
        return msg!.Data.AsSpan(4).ToArray();
    }

    private static void SendNoResp(Iso15765Channel iso, byte[] req)
    {
        var begin = iso.BeginTransmit(PhysReq, req);
        Assert.True(begin.Started);
        iso.BusEgress!(begin.CanFrame!);
        iso.EndTransmit(begin.Filter!);
        iso.ReassembledPayloadQueue.TryDequeue(out _);
    }

    private static void DriveProgrammingPreconditions(Iso15765Channel iso)
    {
        Send(iso, [0x10, 0x03]);
        Send(iso, [0x28]);
        Send(iso, [0x27, 0x01]);
        Send(iso, [0x27, 0x02, 0xAB, 0xCD]);
        Send(iso, [0xA5, 0x01]);
        SendNoResp(iso, [0xA5, 0x03]);
    }

    /// <summary>
    /// Builds the exact $36 USDT payload shape observed in the user's wire log:
    /// SID + sub $00 + 3-byte address 0x003FB8 + 1025 bytes of data, totalling
    /// 1030 bytes. The anchor model treats 0x003FB8 as the base and stores the
    /// 1025 data bytes at offset 0 - succeeding even though the address is
    /// well outside the 5344-byte declared buffer.
    /// </summary>
    private static byte[] BuildLargeAddressTransfer()
    {
        var payload = new byte[1030];
        payload[0] = 0x36;
        payload[1] = 0x00;       // sub = Download
        payload[2] = 0x00;
        payload[3] = 0x3F;
        payload[4] = 0xB8;       // 3-byte addr = 0x003FB8 = 16312
        for (int i = 5; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
        return payload;
    }

    [Fact]
    public void Absolute_address_36_anchors_and_returns_positive()
    {
        // The bug-report scenario: a real-DPS-shaped $36 with an absolute
        // RAM address that's way past the $34-declared buffer. Anchor
        // mode treats the address as the base; data lands at offset 0.
        var (_, node, _, iso) = Wire();

        DriveProgrammingPreconditions(iso);
        Assert.Equal(new byte[] { 0x74 }, Send(iso, [0x34, 0x00, 0x00, 0x14, 0xE0]));   // 5344 buffer

        Assert.Equal(new byte[] { 0x76 }, Send(iso, BuildLargeAddressTransfer()));

        Assert.Equal(1025u, node.State.DownloadBytesReceived);
        Assert.Equal(0x003FB8u, node.State.DownloadCaptureBaseAddress);
        Assert.NotNull(node.State.DownloadBuffer);
        // The 1025 payload bytes (5..1029 of the request) landed at offset 0.
        Assert.Equal((byte)(5 & 0xFF), node.State.DownloadBuffer[0]);
        Assert.Equal((byte)(6 & 0xFF), node.State.DownloadBuffer[1]);
    }

    [Fact]
    public void Address_before_anchor_returns_NRC_31()
    {
        // The one NRC $31 path that survives: a host that wrote BEFORE its
        // own first $36's address. No observed GM flow does this; silently
        // rebasing would mask a host bug.
        var (_, _, _, iso) = Wire();

        DriveProgrammingPreconditions(iso);
        Send(iso, [0x34, 0x00, 0x00, 0x14, 0xE0]);

        // First $36 anchors at 0x000200.
        Send(iso, [0x36, 0x00, 0x00, 0x02, 0x00, 0xAA, 0xBB]);
        // Second $36 at 0x000100 - before the anchor.
        Assert.Equal(new byte[] { 0x7F, 0x36, 0x31 },
            Send(iso, [0x36, 0x00, 0x00, 0x01, 0x00, 0xCC, 0xDD]));
    }

    [Fact]
    public void Capture_directory_set_writes_a_file_per_36()
    {
        var (bus, node, _, iso) = Wire();
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimCapTest_" + Guid.NewGuid().ToString("N"));
        bus.Capture.CaptureDirectory = tmp;
        string? writtenPath = null;
        bus.Capture.CaptureWritten += p => writtenPath = p;

        try
        {
            DriveProgrammingPreconditions(iso);
            Send(iso, [0x34, 0x00, 0x00, 0x14, 0xE0]);
            Send(iso, BuildLargeAddressTransfer());

            Assert.NotNull(writtenPath);
            Assert.True(File.Exists(writtenPath));
            var bytes = File.ReadAllBytes(writtenPath!);
            // The per-$36 file contains exactly the dataRecord (1025 bytes).
            Assert.Equal(1025, bytes.Length);
            for (int i = 0; i < 1025; i++)
                Assert.Equal((byte)((i + 5) & 0xFF), bytes[i]);

            // Filename embeds the absolute address and byte count.
            string fname = Path.GetFileName(writtenPath!);
            Assert.Contains("003FB8", fname);
            Assert.Contains("1025", fname);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Capture_directory_unset_writes_no_files()
    {
        // The unit-test default: CaptureDirectory is null, so no disk side
        // effects regardless of how many $36 transfers run. Production WPF
        // unconditionally sets the directory on startup.
        var (bus, _, _, iso) = Wire();
        Assert.Null(bus.Capture.CaptureDirectory);
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimCapTest_" + Guid.NewGuid().ToString("N"));

        DriveProgrammingPreconditions(iso);
        Send(iso, [0x34, 0x00, 0x00, 0x00, 0x10]);    // 16-byte declared buffer
        Send(iso, [0x36, 0x00, 0x00, 0x00, 0x00,
                   0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44]);
        SendNoResp(iso, [0x20]);

        Assert.False(Directory.Exists(tmp));
    }

    /// <summary>
    /// Regression: real T43-class ECUs use a 4-byte $36 startingAddress
    /// (e.g. the SPS kernel destination 0x003FAFE0 from 6Speed.T43's
    /// Pushspskernel). Hard-wiring 3 bytes used to absorb the low byte
    /// 0xE0 into the data record, mis-parsing the address as 0x003FAF and
    /// shifting every subsequent write by one byte. With per-ECU
    /// DownloadAddressByteCount = 4 the address, the offset, and the
    /// first data byte all line up.
    /// </summary>
    [Fact]
    public void Capture_with_4byte_address_parses_correctly_for_T43_kernel()
    {
        var bus = new VirtualBus();
        var algo = new FakeSeedKeyAlgorithm();
        var node = NodeFactory.CreateNodeWithGenericModule(algo);
        // Real T43: kernel destination is 0x003FAFE0 - needs all 4 bytes.
        Assert.Equal(4, node.State.DownloadAddressByteCount);
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.ISO15765, Baud = 500_000, Bus = bus };
        var iso = new Iso15765Channel(new IsoTpTimingParameters());
        iso.BusEgress = frame => bus.DispatchHostTx(frame, ch);
        ch.IsoChannel = iso;
        ch.IsoChannelInbound = (canId, frame) => iso.OnInboundCanFrame(canId, frame.AsSpan(4));
        iso.AddFilter(new Iso15765Channel.IsoFilter
        {
            Id = 1, MaskCanId = 0xFFFFFFFF, PatternCanId = UsdtResp,
            FlowCtlCanId = PhysReq, Format = AddressFormat.Normal,
        });

        DriveProgrammingPreconditions(iso);
        Send(iso, [0x34, 0x00, 0x00, 0x0C, 0x20]);    // T43 first-kernel declared size 3104

        // $36 with 4-byte address 0x003FAFE0, 8 bytes of recognisable data.
        Assert.Equal(new byte[] { 0x76 }, Send(iso,
            [0x36, 0x00, 0x00, 0x3F, 0xAF, 0xE0,
             0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE]));

        Assert.Equal(0x003FAFE0u, node.State.DownloadCaptureBaseAddress);
        Assert.Equal(8u, node.State.DownloadBytesReceived);
        Assert.NotNull(node.State.DownloadBuffer);
        // Data byte 0 in the buffer must be 0xDE, NOT 0xE0 (the 4th address
        // byte) - that's the bug this regression guards against.
        Assert.Equal(0xDE, node.State.DownloadBuffer[0]);
        Assert.Equal(0xAD, node.State.DownloadBuffer[1]);
    }
}
