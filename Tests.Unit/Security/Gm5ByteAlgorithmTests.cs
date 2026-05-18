using System.Text.Json;
using Common.Protocol;
using Core.Security;
using Core.Security.Algorithms;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Security;

// Pins the GMW3110 5-byte SecurityAccess algorithm against several seed/key
// pairs, then exercises the whole exchange through Service27Handler.
public sealed class Gm5ByteAlgorithmTests
{
    private const int Algo92 = 0x92;
    private static string Algo92Password => Gm5ByteAlgorithm.DefaultAlgo92Password;

    public static IEnumerable<object[]> KnownPairsAlgo92 => new[]
    {
        new object[] { "1122334406", "ECBFF787A4" },
        new object[] { "438930D306", "CDCF835F22" },
        new object[] { "91814EB906", "B07B1E76BC" },
        new object[] { "C0CAD29E06", "1E84FDBC03" },
        new object[] { "DEF08CD306", "F3B2B455C8" },
        new object[] { "9C926FF506", "0D1659D3B9" },
        new object[] { "D8B1D54006", "23B71FFCF4" },
        new object[] { "8AE539F506", "EC42A7E9F1" },
    };

    [Theory]
    [MemberData(nameof(KnownPairsAlgo92))]
    public void ComputeKey_Algo92_MatchesReferenceVectors(string seedHex, string expectedKeyHex)
    {
        var seed     = Convert.FromHexString(seedHex);
        var expected = Convert.FromHexString(expectedKeyHex);
        Span<byte> key = stackalloc byte[5];

        Gm5ByteAlgorithm.ComputeKey(seed, Algo92, Algo92Password, key);

        Assert.Equal(expected, key.ToArray());
    }

    [Fact]
    public void ComputeExpectedKey_DefaultConfig_ProducesAlgo92Key()
    {
        var algo = new Gm5ByteAlgorithm();   // default = Algo 92
        Span<byte> key = stackalloc byte[5];
        bool ok = algo.ComputeExpectedKey(level: 1,
                                          seed: Convert.FromHexString("1122334406"),
                                          keyBuffer: key,
                                          out int len);
        Assert.True(ok);
        Assert.Equal(5, len);
        Assert.Equal(Convert.FromHexString("ECBFF787A4"), key.ToArray());
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x42)]
    [InlineData(0x83)]
    [InlineData(0x92)]
    [InlineData(0xAA)]
    [InlineData(0xFF)]
    public void EveryAlgoId_HasValidPasswordAndRoundTrips(int algoId)
    {
        var algo = new Gm5ByteAlgorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new
        {
            algoId    = $"0x{algoId:X2}",
            fixedSeed = "1122334406",
        }));

        Span<byte> seed = stackalloc byte[5];
        algo.GenerateSeed(level: 1, seed, out _);

        Span<byte> key = stackalloc byte[5];
        bool ok = algo.ComputeExpectedKey(level: 1, seed, key, out int len);

        Assert.True(ok, $"algoId 0x{algoId:X2} failed to compute a key");
        Assert.Equal(5, len);
        // Sanity: not all-zero, not bitwise-NOT of seed.
        Assert.NotEqual(new byte[5], key.ToArray());
        var notSeed = new byte[5];
        for (int i = 0; i < 5; i++) notSeed[i] = (byte)~seed[i];
        Assert.NotEqual(notSeed, key.ToArray());
    }

    [Fact]
    public void PasswordTable_HasAll256Entries()
    {
        Assert.Equal(256, Gm5BytePasswords.Table.Count);
        for (int i = 0; i <= 0xFF; i++)
            Assert.True(Gm5BytePasswords.Table.ContainsKey(i), $"missing entry for algoId 0x{i:X2}");
    }

    [Fact]
    public void GenerateSeed_ForcesFamilyByte0x06()
    {
        var algo = new Gm5ByteAlgorithm();
        Span<byte> seed = stackalloc byte[5];
        for (int i = 0; i < 50; i++)
        {
            algo.GenerateSeed(level: 1, seed, out int len);
            Assert.Equal(5, len);
            Assert.Equal(0x06, seed[4]);
        }
    }

    [Fact]
    public void EndToEnd_FixedSeed_CorrectKey_Unlocks()
    {
        var algo = new Gm5ByteAlgorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "1122334406" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-e92"));
        var ch = NodeFactory.CreateChannel();

        // requestSeed -> 67 01 11 22 33 44 06
        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x11, 0x22, 0x33, 0x44, 0x06 },
                     TestFrame.DequeueSingleFrameUsdt(ch));

        // sendKey with the right key -> 67 02
        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0xEC, 0xBF, 0xF7, 0x87, 0xA4 }, ch, nowMs: 0);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void EndToEnd_FixedSeed_WrongKey_Nrc35()
    {
        var algo = new Gm5ByteAlgorithm();
        algo.LoadConfig(JsonSerializer.SerializeToElement(new { fixedSeed = "1122334406" }));
        var node = NodeFactory.CreateNode(
            module: new Core.Security.Modules.Gmw3110_2010_Generic(algo, id: "gm-e92"));
        var ch = NodeFactory.CreateChannel();

        Service27Handler.Handle(node, new byte[] { 0x27, 0x01 }, ch, nowMs: 0);
        TestFrame.DequeueSingleFrameUsdt(ch);

        Service27Handler.Handle(node, new byte[] { 0x27, 0x02, 0xDE, 0xAD, 0xBE, 0xEF, 0xCC }, ch, nowMs: 0);
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(Service.NegativeResponse, resp[0]);
        Assert.Equal(Service.SecurityAccess, resp[1]);
        Assert.Equal((byte)Nrc.InvalidKey, resp[2]);
    }
}
