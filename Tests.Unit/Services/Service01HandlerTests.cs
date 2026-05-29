using Common.Protocol;
using Common.Signals;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// SAE J1979 Mode $01 ShowCurrentData, rebuilt as the signal-backed projection: values come from the ECU's EngineModel
// and DiscreteState through the legislated J1979 formulas in J1979Catalogue, and the $00/$20/... support masks are
// computed from the ECU's advertised subset. A fresh node boots at idle and advertises the whole catalogue subset.
public sealed class Service01HandlerTests
{
    [Fact]
    public void EngineRpm_AtIdle_EncodesPerJ1979()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        // Idle RPM is 750; J1979 $0C decodes (256A+B)/4, so the raw value is 750*4 = 3000 = 0x0BB8.
        Service01Handler.Handle(node, new byte[] { 0x01, 0x0C }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x41, 0x0C, 0x0B, 0xB8 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void CoolantTemp_EncodesWithMinus40Offset()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        // Warm coolant is 90 C; J1979 $05 decodes A-40, so the raw value is 90+40 = 130 = 0x82.
        Service01Handler.Handle(node, new byte[] { 0x01, 0x05 }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x41, 0x05, 0x82 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void MultiPid_ReturnsEachInRequestOrder()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        // RPM ($0C, two bytes) then vehicle speed ($0D, one byte, 0 at idle).
        Service01Handler.Handle(node, new byte[] { 0x01, 0x0C, 0x0D }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x41, 0x0C, 0x0B, 0xB8, 0x0D, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void UnsupportedPid_InMultiRequest_IsDroppedSilently()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        // $02 (freeze-frame DTC) is not in the catalogue, so it drops out while $0C still answers.
        Service01Handler.Handle(node, new byte[] { 0x01, 0x0C, 0x02 }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x41, 0x0C, 0x0B, 0xB8 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void SupportBitmask00_MatchesAdvertisedSubset()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service01Handler.Handle(node, new byte[] { 0x01, 0x00 }, ch, timeMs: 0, isFunctional: false);

        // The wire mask must equal what the catalogue computes for this ECU's subset - it can never drift from it.
        var mask = new byte[4];
        J1979Catalogue.ComputeSupportMask(0x00, node.Mode1Supported, mask);
        Assert.Equal(new byte[] { 0x41, 0x00, mask[0], mask[1], mask[2], mask[3] },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Override_FlowsToTheWire()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        // Pinning the RPM signal changes what $01 $0C reports: 3000 rpm -> raw 12000 = 0x2EE0.
        node.EngineModel.SetOverride(SignalId.EngineRpm, 3000);
        Service01Handler.Handle(node, new byte[] { 0x01, 0x0C }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x41, 0x0C, 0x2E, 0xE0 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void NarrowedSubset_DropsPidsOutsideIt()
    {
        var node = NodeFactory.CreateNode();
        node.Mode1Supported = new HashSet<byte> { 0x0C };
        var ch = NodeFactory.CreateChannel();

        // Coolant is no longer advertised, so a physical request for it alone gets NRC $31.
        Service01Handler.Handle(node, new byte[] { 0x01, 0x05 }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.Obd01ShowCurrentData, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void NoneSupported_Physical_ReturnsNrc31()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service01Handler.Handle(node, new byte[] { 0x01, 0x02 }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.Obd01ShowCurrentData, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void NoneSupported_Functional_IsSilent()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service01Handler.Handle(node, new byte[] { 0x01, 0x02 }, ch, timeMs: 0, isFunctional: true);

        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Malformed_Physical_ReturnsNrc12()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service01Handler.Handle(node, new byte[] { 0x01 }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.Obd01ShowCurrentData,
                                  Nrc.SubFunctionNotSupportedInvalidFormat },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }
}
