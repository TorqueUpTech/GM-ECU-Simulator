using Common.Protocol;
using Core.Ecu;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// Covers EcuNode.RekeyPidAddress, the fix for the editor's Address-column bug. The per-mode PID stores are keyed by
// Pid.Address, but the editor edits Address in place on an already-registered PID. Before the fix the dict entry stayed
// under its old key, so GetPid / GetPidByWireId missed the new address and the ECU NRC'd $2D / $22 reads with
// RequestOutOfRange even though the saved config looked correct (serialization reads the field, not the key).
public sealed class RekeyPidAddressTests
{
    // The reported repro: a $2D row is born at AddPid's auto-assigned default address, then the user retypes the
    // Address to 0x1000. After re-keying, the $2D handler's GetPid(memoryAddress) must resolve at the new address and
    // nothing must linger under the old key.
    [Fact]
    public void Mode2D_AddressEdit_MovesEntryToNewKey()
    {
        var node = NodeFactory.CreateNode();
        var pid = new Pid { Mode = PidMode.Mode2D, Address = 0x0001, Name = "RPM", LengthBytes = 2 };
        node.AddPid(pid);

        pid.Address = 0x1000;
        node.RekeyPidAddress(pid, oldAddress: 0x0001);

        Assert.Same(pid, node.GetPid(0x1000));
        Assert.Null(node.GetPid(0x0001));
    }

    // $2D rows are read on the wire at 0xF000 | (addr & 0x0FFF). For 0x1000 that alias is 0xF000; the read path has to
    // resolve to the re-keyed entry, not the stale one.
    [Fact]
    public void Mode2D_AddressEdit_ReadableViaDerivedWireAlias()
    {
        var node = NodeFactory.CreateNode();
        var pid = new Pid { Mode = PidMode.Mode2D, Address = 0x0001, Name = "RPM", LengthBytes = 2 };
        node.AddPid(pid);

        pid.Address = 0x1000;
        node.RekeyPidAddress(pid, oldAddress: 0x0001);

        Assert.Same(pid, node.GetPidByWireId(0xF000));
    }

    // Same flaw applies to a Mode22 row whose wire PID id is edited via the Identifier dropdown.
    [Fact]
    public void Mode22_AddressEdit_MovesWireId()
    {
        var node = NodeFactory.CreateNode();
        var pid = new Pid { Mode = PidMode.Mode22, Address = 0x1234, Name = "x", LengthBytes = 2 };
        node.AddPid(pid);

        pid.Address = 0x5678;
        node.RekeyPidAddress(pid, oldAddress: 0x1234);

        Assert.Same(pid, node.GetPidByWireId(0x5678));
        Assert.Null(node.GetPidByWireId(0x1234));
    }

    // A Mode1A DID re-point (the $1A identity rows) must relocate in the byte-keyed store too.
    [Fact]
    public void Mode1A_AddressEdit_MovesDid()
    {
        var node = NodeFactory.CreateNode();
        var pid = new Pid { Mode = PidMode.Mode1A, Address = 0x0090, Name = "VIN", LengthBytes = 17 };
        node.AddPid(pid);

        pid.Address = 0x00B0;
        node.RekeyPidAddress(pid, oldAddress: 0x0090);

        Assert.Same(pid, node.GetMode1APid(0xB0));
        Assert.Null(node.GetMode1APid(0x90));
    }

    // Re-keying to the same address is a harmless no-op; the entry stays put.
    [Fact]
    public void UnchangedAddress_IsNoOp()
    {
        var node = NodeFactory.CreateNode();
        var pid = new Pid { Mode = PidMode.Mode2D, Address = 0x1000, Name = "x", LengthBytes = 2 };
        node.AddPid(pid);

        node.RekeyPidAddress(pid, oldAddress: 0x1000);

        Assert.Same(pid, node.GetPid(0x1000));
    }
}
