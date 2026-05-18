using Core.Security;
using Xunit;

namespace EcuSimulator.Tests.Security;

public sealed class RegistryTests
{
    [Theory]
    [InlineData("gm-e38-2byte")]
    [InlineData("gm-e67-2byte")]
    [InlineData("gm-e92-5byte")]
    [InlineData("gm-t43-2byte")]
    [InlineData("gm-bypass-2byte")]
    [InlineData("gm-bypass-5byte")]
    public void BuiltInId_Resolves(string id)
    {
        var module = SecurityModuleRegistry.Create(id);
        Assert.NotNull(module);
        Assert.Equal(id, module!.Id);
    }

    [Theory]
    [InlineData("gm-bypass-2byte", SecurityModuleBehaviour.BypassAll)]
    [InlineData("gm-bypass-5byte", SecurityModuleBehaviour.BypassAll)]
    [InlineData("gm-e38-2byte",    SecurityModuleBehaviour.Strict)]
    [InlineData("gm-e67-2byte",    SecurityModuleBehaviour.Strict)]
    [InlineData("gm-e92-5byte",    SecurityModuleBehaviour.Strict)]
    [InlineData("gm-t43-2byte",    SecurityModuleBehaviour.Strict)]
    public void Behaviour_MatchesExpected(string id, SecurityModuleBehaviour expected)
    {
        var module = SecurityModuleRegistry.Create(id);
        Assert.NotNull(module);
        Assert.Equal(expected, module!.Behaviour);
    }

    // Legacy IDs from every prior naming pass. ConfigStore relies on these
    // being remapped at construction time so old ecu_config.json files and
    // old hardcoded references keep working.
    [Theory]
    [InlineData("gm-e38",                       "gm-e38-2byte")]
    [InlineData("gm-e67",                       "gm-e67-2byte")]
    [InlineData("gm-t43",                       "gm-t43-2byte")]
    [InlineData("gm-e92",                       "gm-e92-5byte")]
    [InlineData("gm-algo-92",                   "gm-e92-5byte")]
    [InlineData("gm-algo92-2byte",              "gm-e38-2byte")]
    [InlineData("gm-algo89-2byte",              "gm-e67-2byte")]
    [InlineData("gm-algo92-5byte",              "gm-e92-5byte")]
    [InlineData("gm-programming-bypass",        "gm-bypass-2byte")]
    [InlineData("gm-permissive-5byte",          "gm-bypass-5byte")]
    [InlineData("gmw3110-2010-not-implemented", "gm-bypass-2byte")]
    public void LegacyId_RemapsToCurrent(string legacyId, string currentId)
    {
        var module = SecurityModuleRegistry.Create(legacyId);
        Assert.NotNull(module);
        Assert.Equal(currentId, module!.Id);
        Assert.Equal(currentId, SecurityModuleRegistry.NormaliseLegacyId(legacyId));
    }

    [Fact]
    public void UnknownId_ReturnsNull()
    {
        Assert.Null(SecurityModuleRegistry.Create("definitely-not-registered-12345"));
    }

    [Fact]
    public void NullId_ReturnsNull()
    {
        Assert.Null(SecurityModuleRegistry.Create(null));
    }

    [Fact]
    public void KnownIds_ContainsCanonical()
    {
        Assert.Contains("gm-e38-2byte",    SecurityModuleRegistry.KnownIds);
        Assert.Contains("gm-bypass-2byte", SecurityModuleRegistry.KnownIds);
    }
}
