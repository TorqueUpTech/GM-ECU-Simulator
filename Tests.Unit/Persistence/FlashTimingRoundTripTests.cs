using Core.Persistence;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Persistence;

// Guards the per-ECU flash-timing knobs through ConfigStore's node<->dto
// round-trip so a saved profile survives a save/load cycle.
public sealed class FlashTimingRoundTripTests
{
    [Fact]
    public void FlashDelays_RoundTripThroughConfigStore()
    {
        var node = NodeFactory.CreateNode();
        node.FlashTransferDelayMs = 35;
        node.FlashEraseDelayMs = 5000;

        var dto = ConfigStore.EcuDtoFrom(node);
        Assert.Equal(35, dto.FlashTransferDelayMs);
        Assert.Equal(5000, dto.FlashEraseDelayMs);

        var back = ConfigStore.EcuNodeFrom(dto);
        Assert.Equal(35, back.FlashTransferDelayMs);
        Assert.Equal(5000, back.FlashEraseDelayMs);
    }

    [Fact]
    public void FlashDelays_DefaultToZero()
    {
        var node = NodeFactory.CreateNode();
        Assert.Equal(0, node.FlashTransferDelayMs);
        Assert.Equal(0, node.FlashEraseDelayMs);

        // A dto with the fields unset (older config) loads as 0, not garbage.
        var back = ConfigStore.EcuNodeFrom(ConfigStore.EcuDtoFrom(node));
        Assert.Equal(0, back.FlashTransferDelayMs);
        Assert.Equal(0, back.FlashEraseDelayMs);
    }
}
