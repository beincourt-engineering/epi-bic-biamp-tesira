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
    public void The_identity_carries_a_public_MacAddresses_list_of_strings()
    {
        // Telemetry reads a MacAddresses property of strings by name off the DeviceInfo it is handed.
        var type = AssemblyFixture.PluginAssembly.GetType(IdentityType);
        var macs = type!.GetProperty("MacAddresses");

        macs.Should().NotBeNull();
        macs!.PropertyType.FullName.Should().StartWith("System.Collections.Generic.List`1[[System.String");
        macs.GetMethod!.IsPublic.Should().BeTrue();
    }

    [Fact]
    public void The_component_lists_every_interface_mac_and_takes_the_dante_macs()
    {
        var source = File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, "TesiraDspDeviceInfo.cs"));

        source.Should().Contain("interfaceMacs = status.MacAddresses;");
        source.Should().Contain("TesiraDeviceInfoParser.MacList(interfaceMacs, dantePrimaryMac, danteSecondaryMac,");
        source.Should().Contain("voipNics.Select(p => p.Value.Mac)");
        source.Should().Contain("controlNics = TesiraNicParser.ControlNics(message);");
        source.Should().Contain("NetworkFeedback = new StringFeedback(\"network\", () => network);");
        source.Should().Contain("identity.MacAddresses = list;");
        source.Should().Contain("public void SetDanteMacAddresses(string primary, string secondary)");

        // the Dante monitor hands over the MACs each danteInfo reply carries
        var monitor = File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, "TesiraDanteMonitor.cs"));
        monitor.Should().Contain("devInfo.SetDanteMacAddresses(info.PrimaryMac, info.SecondaryMac);");
        monitor.Should().Contain("devInfo.SetDanteNetwork(TesiraNicParser.DanteNics(message));");

        // each VoIP card's network is asked of its control/status block and handed over
        var voip = File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, "TesiraVoipMonitor.cs"));
        voip.Should().Contain("string.Format(\"{0} get network\", tag)");
        voip.Should().Contain("devInfo.SetVoipNic(tag, nic);");
        var dsp = File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, "TesiraDsp.cs"));
        dsp.Should().Contain("b.Enabled && b.IsVoip");
        dsp.Should().Contain("voip.AskNetwork();");
        dsp.Should().Contain("Feedbacks.Add(DevInfo.NetworkFeedback);");
    }

    [Fact]
    public void The_timeline_readings_go_on_the_dsp_s_own_list_and_are_asked_at_the_lowest_priority()
    {
        // telemetry reads a device's feedbacks by key off the DSP: the timeline monitor's must be on its list
        var dsp = File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, "TesiraDsp.cs"));
        dsp.Should().Contain("Timeline = new TesiraTimelineMonitor(this);");
        dsp.Should().Contain("foreach (var feedback in Timeline.Feedbacks)");
        dsp.Should().Contain("voip.AskCardStat();");
        dsp.Should().Contain("Timeline?.AskLinks();");

        var timeline = File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, "TesiraTimelineMonitor.cs"));
        timeline.Should().Contain("\"DEVICE get networkStatus\", \"networkStatus\", this, priority: (int)CommandPriority.Low");
        foreach (var key in new[] { "controlLinkUp", "controlLinkMbps", "danteLinkUp", "danteSecondaryLinkUp", "voipFramesPerMin" })
        {
            timeline.Should().Contain($"= \"{key}\";");
        }

        // VoIP lines, calls and the card's uptime were dropped (2026-11-18): only its traffic stays
        foreach (var gone in new[] { "voipLines\"", "voipLinesReady", "voipCallsActive", "voipCardUptimeS" })
        {
            timeline.Should().NotContain(gone);
        }

        var voip = File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, "TesiraVoipMonitor.cs"));
        voip.Should().Contain("string.Format(\"{0} get cardStat\", tag), \"cardStat\", this, priority: (int)CommandPriority.Low");

        // a networkStatus read every minute tells telemetry of a device info change only when something changed
        var info = File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, "TesiraDspDeviceInfo.cs"));
        info.Should().Contain("if (macsChanged || before != string.Join(");
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
