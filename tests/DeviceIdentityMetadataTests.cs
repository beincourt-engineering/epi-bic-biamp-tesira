using FluentAssertions;
using Xunit;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Tests;

/// <summary>
/// The DSP's device info carries its model: the object IDeviceInfoProvider hands out is a
/// TesiraDspDeviceIdentity, a DeviceInfo with a public string Model. The plugin needs a processor to
/// run, so this reads the built DLL's metadata and pins the source.
/// </summary>
public class DeviceIdentityMetadataTests
{
    private const string IdentityType = "Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.TesiraDspDeviceIdentity";

    [Fact]
    public void The_identity_is_a_DeviceInfo_with_a_public_string_Model()
    {
        var type = AssemblyFixture.PluginAssembly.GetType(IdentityType);

        type.Should().NotBeNull();
        type!.BaseType!.FullName.Should().Be("PepperDash.Essentials.Core.DeviceInfo.DeviceInfo");

        var model = type.GetProperty("Model");
        model.Should().NotBeNull();
        model!.PropertyType.FullName.Should().Be("System.String");
        model.GetMethod!.IsPublic.Should().BeTrue();
    }

    [Fact]
    public void The_component_hands_out_the_identity_and_fills_its_model()
    {
        var path = Path.Combine(AssemblyFixture.SourceDirectory, "TesiraDspDeviceInfo.cs");
        var source = File.ReadAllText(path);

        source.Should().Contain("new TesiraDspDeviceIdentity()");
        source.Should().Contain("public DeviceInfo DeviceInfo { get { return identity; } }");
        source.Should().Contain("identity.Model = model;");
        source.Should().Contain("SendFullCommand(\"get\", \"deviceInfo\"");
    }

    [Fact]
    public void A_discovered_server_s_type_number_is_never_taken_as_the_model()
    {
        // discoveredServers lists every Tesira the DSP can see with a number, not a model name.
        var path = Path.Combine(AssemblyFixture.SourceDirectory, "TesiraDspDeviceInfo.cs");
        File.ReadAllText(path).Should().NotContain("discoveredServers");
    }
}
