using Common.Protocol;
using Common.Signals;
using Core.Persistence;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// The editor's whole-ECU view toggles which J1979 PIDs an ECU advertises via EcuNode.SetMode1Supported. That set is
// per-ECU (a copy of the catalogue default) and drives what Service01 answers + the support bitmask.
public sealed class Mode1SupportedTests
{
    [Fact]
    public void SetMode1Supported_TogglesWhatService01Answers()
    {
        var node = NodeFactory.CreateNode();

        // $0C (RPM) is in the default set; dropping it makes a physical $01 $0C return NRC $31.
        node.SetMode1Supported(0x0C, false);
        var ch = NodeFactory.CreateChannel();
        Service01Handler.Handle(node, new byte[] { 0x01, 0x0C }, ch, timeMs: 0, isFunctional: false);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.Obd01ShowCurrentData, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        // Re-enabling it brings the answer back (idle RPM 750 -> 0x0BB8).
        node.SetMode1Supported(0x0C, true);
        var ch2 = NodeFactory.CreateChannel();
        Service01Handler.Handle(node, new byte[] { 0x01, 0x0C }, ch2, timeMs: 0, isFunctional: false);
        Assert.Equal(new byte[] { 0x41, 0x0C, 0x0B, 0xB8 }, TestFrame.DequeueSingleFrameUsdt(ch2));
    }

    [Fact]
    public void Mode1Supported_IsPerEcu_NotShared()
    {
        var a = NodeFactory.CreateNode();
        var b = NodeFactory.CreateNode();

        a.SetMode1Supported(0x0C, false);

        Assert.DoesNotContain((byte)0x0C, a.Mode1Supported);
        Assert.Contains((byte)0x0C, b.Mode1Supported);                  // b is unaffected by a's toggle
        Assert.Contains((byte)0x0C, J1979Catalogue.DefaultSupported);   // the shared default is untouched
    }

    [Fact]
    public void Catalogue_All_ListsDataPids_NeverBitmaskPids()
    {
        Assert.NotEmpty(J1979Catalogue.All);
        Assert.Contains(J1979Catalogue.All, p => p.Pid == 0x0C);                 // RPM is present
        Assert.DoesNotContain(J1979Catalogue.All, p => J1979Catalogue.IsBitmaskPid(p.Pid));
    }

    [Fact]
    public void Mode1Disabled_RoundTripsThroughConfig()
    {
        var node = NodeFactory.CreateNode();
        node.SetMode1Supported(0x0C, false);   // turn RPM off
        node.SetMode1Supported(0x0D, false);   // and vehicle speed

        // Save -> the disabled delta is captured (sorted), nothing else changes.
        var dto = ConfigStore.EcuDtoFrom(node);
        Assert.Equal(new byte[] { 0x0C, 0x0D }, dto.Mode1Disabled!);

        // Load -> the toggles survive: both stay off, an untouched default stays on.
        var reloaded = ConfigStore.EcuNodeFrom(dto);
        Assert.DoesNotContain((byte)0x0C, reloaded.Mode1Supported);
        Assert.DoesNotContain((byte)0x0D, reloaded.Mode1Supported);
        Assert.Contains((byte)0x05, reloaded.Mode1Supported);   // coolant temp untouched
    }

    [Fact]
    public void Mode1Disabled_IsNull_WhenFullDefaultSetAdvertised()
    {
        var node = NodeFactory.CreateNode();   // never toggled -> full default subset
        Assert.Null(ConfigStore.EcuDtoFrom(node).Mode1Disabled);
    }
}
