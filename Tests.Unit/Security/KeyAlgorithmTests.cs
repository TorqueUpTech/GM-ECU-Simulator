using Core.Security;
using Xunit;

namespace EcuSimulator.Tests.Security;

public class KeyAlgorithmTests
{
    [Fact]
    public void Lookup_AlgorithmZero_ReturnsSeedAndKey()
    {
        bool found = KeyAlgorithm.Lookup(0, out byte[] seed, out byte[] seedKey);

        Assert.True(found);
        Assert.Equal(6, seed.Length);
        Assert.Equal(6, seedKey.Length);
    }

    [Fact]
    public void Lookup_AlgorithmMax_ReturnsSeedAndKey()
    {
        bool found = KeyAlgorithm.Lookup(255, out byte[] seed, out byte[] seedKey);

        Assert.True(found);
        Assert.Equal(6, seed.Length);
        Assert.Equal(6, seedKey.Length);
    }

    [Fact]
    public void DeriveKey_WithSeed_ReturnsKey()
    {
        byte[] testSeed = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        byte[] key = KeyAlgorithm.DeriveKey(0, testSeed);

        Assert.NotNull(key);
        Assert.Equal(6, key.Length);
    }

    [Fact]
    public void DeriveKey_SameAlgoAndSeed_ProducesSameKey()
    {
        byte[] testSeed = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        byte[] key1 = KeyAlgorithm.DeriveKey(0, testSeed);
        byte[] key2 = KeyAlgorithm.DeriveKey(0, testSeed);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void ValidateKey_WithCorrectKey_ReturnsTrue()
    {
        byte[] testSeed = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        byte[] derivedKey = KeyAlgorithm.DeriveKey(0, testSeed);

        bool isValid = KeyAlgorithm.ValidateKey(0, testSeed, derivedKey);

        Assert.True(isValid);
    }

    [Fact]
    public void ValidateKey_WithWrongKey_ReturnsFalse()
    {
        byte[] testSeed = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        byte[] wrongKey = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        bool isValid = KeyAlgorithm.ValidateKey(0, testSeed, wrongKey);

        Assert.False(isValid);
    }
}
