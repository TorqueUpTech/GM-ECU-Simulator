using System.Text.Json;
using Common.Protocol;
using Core.Security;
using Core.Security.Algorithms;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Security;

// Pinpoints the T43 algorithm (decompiled from 6Speed.T43's gett43key) with
// hand-traced seed->key pairs, then exercises the whole exchange end-to-end
// via Service27Handler. If/when a real seed/key capture is obtained from
// hardware, add it to ComputedTestVectors below.
public sealed class T43AlgorithmTests
{
    [Theory]
    [InlineData((ushort)0x0000, (ushort)0x4279)]
    [InlineData((ushort)0x1234, (ushort)0xA1E7)]
    [InlineData((ushort)0xDEAD, (ushort)0xDB83)]
    [InlineData((ushort)0xCAFE, (ushort)0x5421)]
    [InlineData((ushort)0xFFFF, (ushort)0x4A79)]
    public void ComputeKey_MatchesDocumentedVectors(ushort seed, ushort expectedKey)
    {
        Assert.Equal(expectedKey, T43Algorithm.ComputeKey(seed));
    }

    [Fact]
    public void EndToEnd_FixedSeed_CorrectKey_Unlocks()
    {
        var algo = new T43Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "1234" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43"));
        var ch = NodeFactory.CreateChannel();

        // requestSeed
        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        // sendKey with the correct T43(0x1234) = 0xA1E7
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0xA1, 0xE7 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void EndToEnd_FixedSeed_WrongKey_Nrc35()
    {
        var algo = new T43Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "1234" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43"));
        var ch = NodeFactory.CreateChannel();

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        TestFrame.DequeueSingleFrameUsdt(ch);

        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0xDE, 0xAD }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(0, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void RandomSeed_RoundTrips_WithComputedKey()
    {
        // No fixedSeed -> random per request. Capture whatever the module emits,
        // recompute the key with the documented algorithm, send it back, expect unlock.
        var algo = new T43Algorithm();
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43"));
        var ch = NodeFactory.CreateChannel();

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        var seedResp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(Service.Positive(Service.SecurityAccess), seedResp[0]);
        Assert.Equal(0x01, seedResp[1]);
        ushort seed = (ushort)((seedResp[2] << 8) | seedResp[3]);
        ushort key = T43Algorithm.ComputeKey(seed);

        Service27Handler.Handle(node,
            new byte[] { 0x27, 0x02, (byte)(key >> 8), (byte)(key & 0xFF) },
            ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    // ----- Programming-session behaviour -----
    //
    // The real T43 boot block is sometimes permissive (issues seed 00 00,
    // accepts key 00 00) but not always - 6Speed.T43 Program.cs:1152/2063
    // calls gett43key whenever the bootloader replies with a non-zero seed.
    // Wrap T43Algorithm in a Strict module (the default) to model the
    // always-runs-the-cipher path. The 00 00 / 00 00 stub path is reachable
    // via a separate Gmw3110_2010_Generic + RandomSeedCipher wrapper with
    // SecurityModuleBehaviour.BypassAll.

    [Fact]
    public void EndToEnd_ProgrammingMode_NonZeroSeed_RequiresRealKey()
    {
        // With Strict behaviour, $10 $02 does not weaken security. A
        // non-zero seed still demands the gett43key-computed key, mirroring
        // 6Speed.T43's gett43key branch at Program.cs:1159.
        var algo = new T43Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "1234" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43-2byte"));
        var ch = NodeFactory.CreateChannel();

        Service10Handler.Handle(node, new byte[] { 0x10, 0x02 }, ch);
        TestFrame.DequeueSingleFrameUsdt(ch);

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        // Hardcoded 00 00 would unlock under BypassAll - under Strict it gets NRC $35.
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0x00, 0x00 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(0, node.State.SecurityUnlockedLevel);

        // The real key (gett43key(0x1234) = 0xA1E7) unlocks.
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0xA1, 0xE7 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void EndToEnd_OperationalMode_RequiresRealKey()
    {
        // Without $10 $02, $27 $02 00 00 should fail when the algorithm has
        // a non-zero fixed seed.
        var algo = new T43Algorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "B34C" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-t43"));
        var ch = NodeFactory.CreateChannel();

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0xB3, 0x4C },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0x00, 0x00 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(0, node.State.SecurityUnlockedLevel);
    }
}
