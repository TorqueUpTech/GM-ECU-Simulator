using Common.Protocol;
using Core.Bus;
using Core.Ecu.Personas;
using Core.Scheduler;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// $01 is legislated OBD-II: on real E38/E67 silicon it lives only on the UDS/OBD dispatcher, so the GMW3110 persona
// answers it on the Uds stack and NRC $11s it on its own stack. These drive the persona dispatch (not the handler
// directly) to prove that gating, which the handler-level tests take as given.
public sealed class Service01DispatchTests
{
    private static DpidScheduler Scheduler() => new VirtualBus().Scheduler;

    [Fact]
    public void UdsStack_Physical_Answers()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        bool handled = Gmw3110Persona.Instance.Dispatch(
            node, new byte[] { 0x01, 0x0C }, ch, isFunctional: false,
            sid: Service.Obd01ShowCurrentData, nowMs: 0, Scheduler(), DiagnosticStack.Uds);

        Assert.True(handled);
        Assert.Equal(new byte[] { 0x41, 0x0C, 0x0B, 0xB8 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Gmw3110Stack_Physical_GetsNrc11()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Gmw3110Persona.Instance.Dispatch(
            node, new byte[] { 0x01, 0x0C }, ch, isFunctional: false,
            sid: Service.Obd01ShowCurrentData, nowMs: 0, Scheduler(), DiagnosticStack.Gmw3110);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.Obd01ShowCurrentData, Nrc.ServiceNotSupported },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Gmw3110Stack_Functional_IsSilent()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Gmw3110Persona.Instance.Dispatch(
            node, new byte[] { 0x01, 0x0C }, ch, isFunctional: true,
            sid: Service.Obd01ShowCurrentData, nowMs: 0, Scheduler(), DiagnosticStack.Gmw3110);

        TestFrame.AssertEmpty(ch);
    }
}
