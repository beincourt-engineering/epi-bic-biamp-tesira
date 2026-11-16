using System.Linq;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Tests;

/// <summary>
/// The Dante monitor reads <c>DEVICE get danteInfo</c> and <c>DEVICE get activeFaultList</c> through this
/// parser. The plugin runs the session non-verbose, so replies carry values in order without names;
/// verbose replies are read too. A reader must refuse another attribute's reply, because replies are
/// matched to commands by order alone.
/// </summary>
public class TesiraDanteParserTests
{
    // The shape of the TTP reference's danteInfo example, cut to two channels each way.
    private const string VerboseDanteInfo =
        "+OK \"danteInfo\":{\"revs\":{\"danteAPIVersion_major\":4 \"danteAPIVersion_minor\":2 \"danteAPIVersion_bugfix\":2 " +
        "\"ubootVersion_major\":0 \"ubootVersion_minor\":0 \"ubootVersion_bugfix\":0 \"biampVersion_major\":1 " +
        "\"biampVersion_minor\":3 \"biampVersion_dot\":0 \"biampVersion_build\":1} " +
        "\"netCfg\":{\"hostname\":\"Tesira-DAN\" \"primaryInterface\":{\"autoConfigure\":true \"ipAddress\":\"192.0.2.26\" " +
        "\"netMask\":\"255.255.255.0\" \"DNSServer\":\"0.0.0.0\" \"defaultGateway\":\"192.0.2.1\"} " +
        "\"secondaryInterface\":{\"autoConfigure\":true \"ipAddress\":\"192.0.2.26\" \"netMask\":\"255.255.255.0\" " +
        "\"DNSServer\":\"0.0.0.0\" \"defaultGateway\":\"192.0.2.1\"}} " +
        "\"allNetStatus\":{\"primaryStatus\":{\"macAddress\":\"00:90:5e:07:df:7e\" \"linkSpeedInMbS\":1000} " +
        "\"secondaryStatus\":{\"macAddress\":\"\" \"linkSpeedInMbS\":0}} " +
        "\"preferredDanteNetworkClockSource\":true \"networkClockSlaveOnly\":false \"networkLatencyInMicroseconds\":1000 " +
        "\"chInfos\":[{\"name\":\"Program L\" \"number\":0 \"txrx\":TX \"faultOnInactive\":false} " +
        "{\"name\":\"\" \"number\":1 \"txrx\":TX \"faultOnInactive\":false} " +
        "{\"name\":\"Lectern Mic\" \"number\":0 \"txrx\":RX \"faultOnInactive\":true} " +
        "{\"name\":\"02\" \"number\":1 \"txrx\":RX \"faultOnInactive\":false}]}";

    // The same reply with the names left out, in the attribute's order.
    private const string DanteInfo =
        "+OK {{4 2 2 0 0 0 1 3 0 1} {\"Tesira-DAN\" {true \"192.0.2.26\" \"255.255.255.0\" \"0.0.0.0\" \"192.0.2.1\"} " +
        "{true \"192.0.2.26\" \"255.255.255.0\" \"0.0.0.0\" \"192.0.2.1\"}} " +
        "{{\"00:90:5e:07:df:7e\" 1000} {\"\" 0}} true false 1000 " +
        "[{\"Program L\" 0 TX false} {\"\" 1 TX false} {\"Lectern Mic\" 0 RX true} {\"02\" 1 RX false}]}";

    private const string NetworkStatus =
        "+OK {2 \"TesiraForte\" \"192.0.2.1\" [{\"control\" {\"00:90:5e:13:3b:27\" LINK_1_GB STATIC \"192.0.2.10\" " +
        "\"255.255.255.0\" \"\" \"\" \"192.0.2.1\"}}] {\"\" \"\" \"\"} true}";

    [Theory]
    [InlineData(VerboseDanteInfo)]
    [InlineData(DanteInfo)]
    public void Reads_the_dante_interface_in_either_style(string reply)
    {
        var info = TesiraDanteParser.DanteInfo(reply);

        info.Should().NotBeNull();
        info!.PreferredLeader.Should().BeTrue();
        info.FollowerOnly.Should().BeFalse();
        info.LatencyMicroseconds.Should().Be(1000);
        info.Transmit.Select(c => c.Name).Should().Equal("Program L", "");
        info.Receive.Select(c => (c.Number, c.Name, c.FaultOnInactive)).Should().Equal((0, "Lectern Mic", true), (1, "02", false));
    }

    [Theory]
    [InlineData(NetworkStatus)]
    [InlineData("+OK \"5.7.1.4\"")]
    [InlineData("+OK")]
    [InlineData("-ERR address not found: {\"deviceId\":0 \"classCode\":0 \"instanceNum\":0}")]
    [InlineData("+OK {1 2 3")]
    [InlineData(null)]
    public void Refuses_a_reply_that_is_not_dante_info(string? reply)
    {
        TesiraDanteParser.DanteInfo(reply).Should().BeNull();
    }

    [Fact]
    public void An_error_reply_means_no_dante_interface()
    {
        TesiraDanteParser.IsError("-ERR address not found").Should().BeTrue();
        TesiraDanteParser.IsError(DanteInfo).Should().BeFalse();
    }

    [Fact]
    public void The_dante_value_lists_the_receive_channels_that_must_carry_audio()
    {
        var json = JObject.Parse(TesiraDanteValues.DanteJson(TesiraDanteParser.DanteInfo(DanteInfo)!));

        json["present"]!.Value<bool>().Should().BeTrue();
        json["rx"]!.Value<int>().Should().Be(2);
        json["tx"]!.Value<int>().Should().Be(2);
        json["preferredLeader"]!.Value<bool>().Should().BeTrue();
        json["latencyUs"]!.Value<int>().Should().Be(1000);
        json["watched"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("[[0,\"Lectern Mic\"]]");
    }

    [Fact]
    public void The_dante_value_stays_whole_json_under_the_limit()
    {
        var channels = string.Join(" ", Enumerable.Range(0, 64).Select(i => $"{{\"Receive channel long name {i:00}\" {i} RX true}}"));
        var reply = "+OK {{} {} {} false false 1000 [" + channels + "]}";

        var text = TesiraDanteValues.DanteJson(TesiraDanteParser.DanteInfo(reply)!);

        text.Length.Should().BeLessOrEqualTo(TesiraDanteValues.MaxValueChars);
        var json = JObject.Parse(text);
        json["rx"]!.Value<int>().Should().Be(64);
        (json["watched"]!.Count() + json["more"]!.Value<int>()).Should().Be(64);
    }

    // activeFaultList: one entry per device, an indicator, its name, the faults under it and the serial.
    private const string NoFaults =
        "+OK [{INDICATOR_NONE_IN_DEVICE \"No fault in device\" [] \"04000001\"}]";

    private const string VerboseNoFaults =
        "+OK \"value\":[{\"id\":INDICATOR_NONE_IN_DEVICE \"name\":\"No fault in device\" \"faults\":[] \"serialNumber\":\"04000001\"}]";

    private const string DanteFault =
        "+OK [{INDICATOR_MAJOR_IN_DEVICE \"Major fault in device\" " +
        "[{FAULT_DANTE_FLOW_INACTIVE \"One or more Dante flows inactive\"}] \"04000001\"} " +
        "{INDICATOR_NONE_IN_DEVICE \"No fault in device\" [] \"04000002\"}]";

    [Theory]
    [InlineData(NoFaults)]
    [InlineData(VerboseNoFaults)]
    public void No_faults_is_ok(string reply)
    {
        var list = TesiraDanteParser.FaultList(reply);

        list.Should().NotBeNull();
        list!.Level.Should().Be("ok");
        list.Faults.Should().BeEmpty();
        TesiraDanteValues.FaultsJson(list).Should().Be("[]");
        TesiraDanteValues.Issues(list).Should().BeEmpty();
    }

    [Fact]
    public void A_major_fault_is_a_fault_and_names_itself()
    {
        var list = TesiraDanteParser.FaultList(DanteFault)!;

        list.Level.Should().Be("fault");
        list.Faults.Should().ContainSingle();
        list.Faults[0].Id.Should().Be("FAULT_DANTE_FLOW_INACTIVE");
        TesiraDanteParser.IsDante(list.Faults[0]).Should().BeTrue();
        TesiraDanteValues.Issues(list).Should().Be("One or more Dante flows inactive");
        JArray.Parse(TesiraDanteValues.FaultsJson(list))[0]!["name"]!.Value<string>().Should().Be("One or more Dante flows inactive");
    }

    [Fact]
    public void An_entry_in_fault_with_nothing_listed_under_it_still_says_what_is_wrong()
    {
        var list = TesiraDanteParser.FaultList("+OK [{INDICATOR_MINOR_IN_DEVICE \"Minor fault in device\" [] \"04000001\"}]")!;

        list.Level.Should().Be("warning");
        list.Faults.Select(f => f.Name).Should().Equal("Minor fault in device");
    }

    [Theory]
    [InlineData(DanteInfo)]
    [InlineData(NetworkStatus)]
    [InlineData("+OK \"04000001\"")]
    [InlineData("+OK [[\"192.0.2.10\" \"TesiraForte\" false 1 11]]")]
    [InlineData("-ERR")]
    public void Refuses_a_reply_that_is_not_a_fault_list(string reply)
    {
        TesiraDanteParser.FaultList(reply).Should().BeNull();
    }

    [Fact]
    public void Strings_keep_escaped_quotes()
    {
        var node = TtpValue.Parse("+OK \"say \\\"hi\\\"\"");
        node!.AsText().Should().Be("say \"hi\"");
    }
}
