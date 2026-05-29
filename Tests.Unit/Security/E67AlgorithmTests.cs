using Common.Protocol;
using Core.Security;
using Core.Security.Algorithms;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using System.Text.Json;
using Xunit;

namespace EcuSimulator.Tests.Security;

// Pinpoints the E67 algorithm (extracted from PowerPCM_Flasher_0006's
// KeyAlgoGm_$89 native function) with seed->key pairs computed from the
// disassembly, then exercises the full exchange end-to-end via
// Service27Handler. Real captured pairs from hardware should be added to
// ComputeKey_MatchesDocumentedVectors when available.
public sealed class E67AlgorithmTests
{
    [Theory]
    [InlineData((ushort)0x0000, (ushort)0x1513)]
    [InlineData((ushort)0x0001, (ushort)0x2513)]
    [InlineData((ushort)0x1234, (ushort)0x5637)]
    [InlineData((ushort)0xABCD, (ushort)0xAFD0)]
    [InlineData((ushort)0xDEAD, (ushort)0xB2FE)]
    [InlineData((ushort)0xCAFE, (ushort)0xC1C3)]
    [InlineData((ushort)0xFFFF, (ushort)0xD513)]
    public void ComputeKey_MatchesDocumentedVectors(ushort seed, ushort expectedKey)
    {
        Assert.Equal(expectedKey, E67Algorithm.ComputeKey(seed));
    }

    // Sanity check that the $89 cipher is genuinely different from the $92
    // cipher (community sources sometimes claim E38/E67 share an algorithm;
    // PowerPCM_Flasher's two distinct native functions prove otherwise).
    [Fact]
    public void DifferentFromE38_ForEverySeed()
    {
        int matches = 0;
        for (int s = 0; s <= 0xFFFF; s++)
        {
            if (E67Algorithm.ComputeKey((ushort)s) == E38Algorithm.ComputeKey((ushort)s))
                matches++;
        }
        Assert.Equal(0, matches);
    }

    [Fact]
    public void EndToEnd_FixedSeed_CorrectKey_Unlocks()
    {
        var algo = new E67Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "1234" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-e67"));
        var ch = NodeFactory.CreateChannel();

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        // E67(0x1234) = 0x5637
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0x56, 0x37 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void EndToEnd_FixedSeed_WrongKey_Nrc35()
    {
        var algo = new E67Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "1234" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-e67"));
        var ch = NodeFactory.CreateChannel();

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        TestFrame.DequeueSingleFrameUsdt(ch);

        // The E38(0x1234) key (0x96CE) is wrong for E67 - distinct algorithms.
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0x96, 0xCE }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(0, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void RandomSeed_RoundTrips_WithComputedKey()
    {
        var algo = new E67Algorithm();
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-e67"));
        var ch = NodeFactory.CreateChannel();

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        var seedResp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(Service.Positive(Service.SecurityAccess), seedResp[0]);
        Assert.Equal(0x01, seedResp[1]);
        ushort seed = (ushort)((seedResp[2] << 8) | seedResp[3]);
        ushort key = E67Algorithm.ComputeKey(seed);

        Service27Handler.Handle(node,
            new byte[] { 0x27, 0x02, (byte)(key >> 8), (byte)(key & 0xFF) },
            ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Registry_Resolves_gm_e67_2byte()
    {
        var module = SecurityModuleRegistry.Create("gm-e67-2byte");
        Assert.NotNull(module);
        Assert.Equal("gm-e67-2byte", module!.Id);
    }
}
