using Common.Persistence;
using Common.Protocol;
using Common.Waveforms;
using Core.Ecu;
using Core.Persistence;
using Core.Pids;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using System.Text;
using Xunit;

namespace EcuSimulator.Tests.Pids;

// Library-fallback behaviour for the three handlers + classifier. The flag
// EcuNode.AutoRespondFromLibrary gates fallback so existing fixtures (which
// assume an unconfigured DID NRCs) stay green; live ECUs flip it on via the
// persistence schema's default-true. User-curated entries always win on
// collision; the library only fills the gaps.
//
// Response shape per library row is decided by PidLibraryClassifier:
// MEASUREMENT-tagged rows with sensor-style metadata get a sin waveform;
// CHARACTERISTIC-tagged rows, name prefixes like "Ke", anything > 4 bytes,
// and every Mode1A row get zero-filled (or, for Mode1A, the canned
// placeholder from DefaultDidValues when one exists).
public sealed class PidLibraryResponderTests
{
    // ---------------- $22 ----------------

    [Fact]
    public void Service22_LibraryHit_OnFixedClassifiedPid_AnswersZeroFilled()
    {
        // PID 0x0011 = KeTPSC_Raw_Slope_TPS_1, CHARACTERISTIC, 1 byte.
        // CHARACTERISTIC + "Ke" prefix both push toward Fixed.
        var node = NodeFactory.CreateNode();
        node.AutoRespondFromLibrary = true;
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0x00, 0x11 }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x62, 0x00, 0x11, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Service22_LibraryHit_OnWaveformClassifiedPid_AnswersPositive()
    {
        // PID 0x000C = SbEPSI_b_LORES_Prd, MEASUREMENT, 2 bytes - classifier
        // routes to Waveform. Pin the wire shape; value bytes change with time.
        var node = NodeFactory.CreateNode();
        node.AutoRespondFromLibrary = true;
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0x00, 0x0C }, ch, timeMs: 0, isFunctional: false);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(5, resp.Length);      // SID + PID hi + PID lo + 2 value bytes
        Assert.Equal(0x62, resp[0]);
        Assert.Equal(0x00, resp[1]);
        Assert.Equal(0x0C, resp[2]);
    }

    [Fact]
    public void Service22_LibraryMiss_StillNrcs_WhenFlagOn()
    {
        var node = NodeFactory.CreateNode();
        node.AutoRespondFromLibrary = true;
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0xFF, 0xFE }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReadDataByParameterIdentifier, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Service22_UserPid_WinsOver_Library()
    {
        var node = NodeFactory.CreateNode();
        node.AutoRespondFromLibrary = true;
        node.AddPid(new Pid
        {
            Address = 0x000C,
            Size = PidSize.Byte,
            DataType = PidDataType.Unsigned,
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant, Offset = 0x42 },
        });
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0x00, 0x0C }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x62, 0x00, 0x0C, 0x42 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Service22_LibraryHit_IgnoredWhenFlagOff()
    {
        var node = NodeFactory.CreateNode();
        Assert.False(node.AutoRespondFromLibrary);
        var ch = NodeFactory.CreateChannel();

        Service22Handler.Handle(node, new byte[] { 0x22, 0x00, 0x0C }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.ReadDataByParameterIdentifier, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    // ---------------- $01 ----------------

    [Fact]
    public void Service01_LibraryHit_OnWaveformClassifiedPid_AnswersPositive()
    {
        // PID $05 = ECT, MEASUREMENT with CM_T_DEG_Ca conversion -> Waveform.
        // Pin the wire shape; value byte changes with time.
        var node = NodeFactory.CreateNode();
        node.AutoRespondFromLibrary = true;
        var ch = NodeFactory.CreateChannel();

        Service01Handler.Handle(node, new byte[] { 0x01, 0x05 }, ch, timeMs: 0, isFunctional: false);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(3, resp.Length);
        Assert.Equal(0x41, resp[0]);
        Assert.Equal(0x05, resp[1]);
    }

    // ---------------- $1A ----------------

    [Fact]
    public void Service1A_LibraryHit_PrefersDefaultDidValuesPlaceholder()
    {
        // DID $B5 = Broadcast Code: in both library (1 byte declared) and
        // DefaultDidValues ("SIMC" = 4 bytes). Responder uses the canned
        // 4-byte string verbatim - 5A B5 53 49 4D 43 fits a single ISO-TP
        // frame so we can assert byte-for-byte.
        var node = NodeFactory.CreateNode();
        node.AutoRespondFromLibrary = true;
        var ch = NodeFactory.CreateChannel();

        Service1AHandler.Handle(node, new byte[] { 0x1A, 0xB5 }, ch, isFunctional: false);

        Assert.Equal(new byte[] { 0x5A, 0xB5, 0x53, 0x49, 0x4D, 0x43 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Service1A_LibraryHit_UnknownDid_ZeroFillsToLibraryLength()
    {
        // DID $32 is in the library (1 byte) but not in DefaultDidValues.
        // Responder falls through to a zero-filled byte at the library's
        // declared length: 5A 32 00.
        var node = NodeFactory.CreateNode();
        node.AutoRespondFromLibrary = true;
        var ch = NodeFactory.CreateChannel();

        Service1AHandler.Handle(node, new byte[] { 0x1A, 0x32 }, ch, isFunctional: false);

        Assert.Equal(new byte[] { 0x5A, 0x32, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void GetMode1AIdentifier_VinDid_ReturnsCannedPlaceholderVerbatim()
    {
        // Direct unit test for the responder helper - bypasses ISO-TP so the
        // 17-byte VIN placeholder can be asserted byte-for-byte without
        // reassembling First Frame + Consecutive Frames.
        var bytes = PidLibraryResponder.GetMode1AIdentifier(0x90);
        Assert.NotNull(bytes);
        Assert.Equal(17, bytes!.Length);

        // The responder hands back the canned default verbatim, and that default is now a valid VIN (correct
        // ISO 3779 check digit) so a format-validating tester accepts it.
        Assert.Equal(DefaultDidValues.Get(0x90), bytes);
        Assert.True(Vin.IsCheckDigitValid(Encoding.ASCII.GetString(bytes)), "default VIN must have a valid check digit");
    }

    // ---------------- Persistence ----------------

    [Fact]
    public void EcuDto_RoundTrip_PreservesFlag_AcrossDefault_And_ExplicitFalse()
    {
        var onDto  = new EcuDto { Name = "On",  PhysicalRequestCanId = 0x7E0, UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8 };
        var offDto = new EcuDto { Name = "Off", PhysicalRequestCanId = 0x7E0, UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8, AutoRespondFromLibrary = false };

        var on  = ConfigStore.EcuNodeFrom(onDto);
        var off = ConfigStore.EcuNodeFrom(offDto);

        Assert.True(on.AutoRespondFromLibrary);
        Assert.False(off.AutoRespondFromLibrary);
    }
}
