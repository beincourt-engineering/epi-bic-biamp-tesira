using System.Linq;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Tests;

/// <summary>
/// The Dante monitor reads <c>DEVICE get danteInfo</c> and <c>DEVICE get activeFaultList</c> through this
/// parser. The plugin runs the session non-verbose, so replies carry values in order without names, with
/// records written as lists and enum values as numbers; verbose replies are read too. A reader must refuse
/// another attribute's reply, because replies are matched to commands by order alone.
/// </summary>
public class TesiraDanteParserTests
{
    // The shape a Tesira FORTE answers in a verbose session, cut to two transmit and three receive channels.
    private const string VerboseDanteInfo =
        "+OK \"danteInfo\":{\"revs\":{\"danteAPIVersion_major\":4 \"danteAPIVersion_minor\":4 \"danteAPIVersion_bugfix\":1 " +
        "\"ubootVersion_major\":1 \"ubootVersion_minor\":3 \"ubootVersion_bugfix\":81 \"biampVersion_major\":1 " +
        "\"biampVersion_minor\":3 \"biampVersion_dot\":0 \"biampVersion_build\":11} " +
        "\"netCfg\":{\"hostname\":\"Tesira-DAN\" \"primaryInterface\":{\"autoConfigure\":true \"ipAddress\":\"192.0.2.26\" " +
        "\"netMask\":\"255.255.255.0\" \"DNSServer\":\"192.0.2.1\" \"defaultGateway\":\"192.0.2.1\"} " +
        "\"secondaryInterface\":{\"autoConfigure\":true \"ipAddress\":\"192.0.2.26\" \"netMask\":\"255.255.255.0\" " +
        "\"DNSServer\":\"192.0.2.1\" \"defaultGateway\":\"192.0.2.1\"}} " +
        "\"allNetStatus\":{\"primaryStatus\":{\"macAddress\":\"00:90:5e:07:df:7e\" \"linkSpeedInMbS\":1000} " +
        "\"secondaryStatus\":{\"macAddress\":\"\" \"linkSpeedInMbS\":0}} " +
        "\"preferredDanteNetworkClockSource\":true \"networkClockSlaveOnly\":false \"networkLatencyInMicroseconds\":1000 " +
        "\"chInfos\":[{\"name\":\"Program L\" \"number\":0 \"txrx\":TX \"faultOnInactive\":false \"flowCount\":0} " +
        "{\"name\":\"\" \"number\":1 \"txrx\":TX \"faultOnInactive\":true \"flowCount\":0} " +
        "{\"name\":\"Lectern Mic\" \"number\":0 \"txrx\":RX \"faultOnInactive\":true \"flowCount\":1} " +
        "{\"name\":\"02\" \"number\":1 \"txrx\":RX \"faultOnInactive\":false \"flowCount\":0} " +
        "{\"name\":\"Spare In \" \"number\":2 \"txrx\":RX \"faultOnInactive\":true \"flowCount\":0}]}";

    // The same reply with the names left out, records as lists, as a non-verbose session writes them.
    private const string DanteInfo =
        "+OK [[4 4 1 1 3 81 1 3 0 11] [\"Tesira-DAN\" [true \"192.0.2.26\" \"255.255.255.0\" \"192.0.2.1\" \"192.0.2.1\"] " +
        "[true \"192.0.2.26\" \"255.255.255.0\" \"192.0.2.1\" \"192.0.2.1\"]] " +
        "[[\"00:90:5e:07:df:7e\" 1000] [\"\" 0]] true false 1000 " +
        "[[\"Program L\" 0 TX false 0] [\"\" 1 TX true 0] [\"Lectern Mic\" 0 RX true 1] [\"02\" 1 RX false 0] " +
        "[\"Spare In \" 2 RX true 0]]]";

    // And with records in braces, as other non-verbose replies are written.
    private const string BracedDanteInfo =
        "+OK {{4 4 1 1 3 81 1 3 0 11} {\"Tesira-DAN\" {true \"192.0.2.26\" \"255.255.255.0\" \"192.0.2.1\" \"192.0.2.1\"} " +
        "{true \"192.0.2.26\" \"255.255.255.0\" \"192.0.2.1\" \"192.0.2.1\"}} " +
        "{{\"00:90:5e:07:df:7e\" 1000} {\"\" 0}} true false 1000 " +
        "[{\"Program L\" 0 TX false 0} {\"\" 1 TX true 0} {\"Lectern Mic\" 0 RX true 1} {\"02\" 1 RX false 0} " +
        "{\"Spare In \" 2 RX true 0}]}";

    private const string NetworkStatus =
        "+OK {2 \"TesiraForte\" \"192.0.2.1\" [{\"control\" {\"00:90:5e:13:3b:27\" LINK_1_GB STATIC \"192.0.2.10\" " +
        "\"255.255.255.0\" \"\" \"\" \"192.0.2.1\"}}] {\"\" \"\" \"\"} true}";

    [Theory]
    [InlineData(VerboseDanteInfo)]
    [InlineData(DanteInfo)]
    [InlineData(BracedDanteInfo)]
    public void Reads_the_dante_interface_in_either_style(string reply)
    {
        var info = TesiraDanteParser.DanteInfo(reply);

        info.Should().NotBeNull();
        info!.PreferredLeader.Should().BeTrue();
        info.FollowerOnly.Should().BeFalse();
        info.LatencyMicroseconds.Should().Be(1000);
        info.Transmit.Select(c => c.Name).Should().Equal("Program L", "");
        info.Receive.Select(c => (c.Number, c.Name, c.FaultOnInactive, c.FlowCount))
            .Should().Equal((0, "Lectern Mic", true, 1), (1, "02", false, 0), (2, "Spare In ", true, 0));
    }

    [Fact]
    public void A_channel_without_a_flow_count_reads_as_not_reported()
    {
        var info = TesiraDanteParser.DanteInfo("+OK [[] [] [] false false 1000 [[\"Lectern Mic\" 0 RX true]]]")!;

        info.Receive.Single().FlowCount.Should().BeNull();
    }

    [Theory]
    [InlineData(NetworkStatus)]
    [InlineData(DanteFault)]
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
    public void The_dante_value_lists_the_channels_that_must_carry_audio_with_their_flows()
    {
        var json = JObject.Parse(TesiraDanteValues.DanteJson(TesiraDanteParser.DanteInfo(DanteInfo)!));

        json["present"]!.Value<bool>().Should().BeTrue();
        json["rx"]!.Value<int>().Should().Be(3);
        json["tx"]!.Value<int>().Should().Be(2);
        json["preferredLeader"]!.Value<bool>().Should().BeTrue();
        json["latencyUs"]!.Value<int>().Should().Be(1000);
        // receive channels first, names trimmed, then transmit
        json["watched"]!.ToString(Newtonsoft.Json.Formatting.None)
            .Should().Be("[[0,\"Lectern Mic\",\"rx\",1],[2,\"Spare In\",\"rx\",0],[1,\"\",\"tx\",0]]");
    }

    [Fact]
    public void The_dante_value_stays_whole_json_under_the_limit()
    {
        var channels = string.Join(" ", Enumerable.Range(0, 64).Select(i => $"[\"Receive channel long name {i:00}\" {i} RX true 1]"));
        var reply = "+OK [[] [] [] false false 1000 [" + channels + "]]";

        var text = TesiraDanteValues.DanteJson(TesiraDanteParser.DanteInfo(reply)!);

        text.Length.Should().BeLessOrEqualTo(TesiraDanteValues.MaxValueChars);
        var json = JObject.Parse(text);
        json["rx"]!.Value<int>().Should().Be(64);
        (json["watched"]!.Count() + json["more"]!.Value<int>()).Should().Be(64);
    }

    // activeFaultList: a list of indicator entries (in the device, in the system), each an id, its name, the
    // faults under it (an id, a name and an instance) and the serial. These two are a Tesira FORTE's answers
    // with one Dante channel set to fault when inactive and nothing subscribed to it.
    private const string VerboseDanteFault =
        "+OK \"value\":[{\"id\":INDICATOR_MAJOR_IN_DEVICE \"name\":\"Major Fault in Device\" " +
        "\"faults\":[{\"id\":FAULT_DANTE_FLOW_INACTIVE \"name\":\"one or more Dante flows inactive\" \"instance\":\"DEFAULT\"}] " +
        "\"serialNumber\":\"04000001\"} " +
        "{\"id\":INDICATOR_MAJOR_IN_SYSTEM \"name\":\"Major Fault in System\" \"faults\":[] \"serialNumber\":\"04000001\"}]";

    private const string DanteFault =
        "+OK [[1 \"Major Fault in Device\" [[86 \"one or more Dante flows inactive\" \"DEFAULT\"]] \"04000001\"] " +
        "[3 \"Major Fault in System\" [] \"04000001\"]]";

    [Theory]
    [InlineData(VerboseDanteFault, "FAULT_DANTE_FLOW_INACTIVE")]
    [InlineData(DanteFault, "86")]
    public void A_major_fault_is_a_fault_and_names_itself(string reply, string id)
    {
        var list = TesiraDanteParser.FaultList(reply)!;

        list.Level.Should().Be("fault");
        list.Faults.Should().ContainSingle();
        list.Faults[0].Id.Should().Be(id);
        list.Faults[0].Instance.Should().Be("DEFAULT");
        TesiraDanteParser.IsDante(list.Faults[0]).Should().BeTrue();
        TesiraDanteValues.Issues(list).Should().Be("one or more Dante flows inactive");
        var fault = JArray.Parse(TesiraDanteValues.FaultsJson(list)).Single();
        fault["name"]!.Value<string>().Should().Be("one or more Dante flows inactive");
        fault["instance"]!.Value<string>().Should().Be("DEFAULT");
    }

    // No fault: not yet seen from a device, so these are the faulted replies' shapes with nothing in them.
    [Theory]
    [InlineData("+OK [[0 \"No Fault in Device\" [] \"04000001\"]]")]
    [InlineData("+OK \"value\":[{\"id\":INDICATOR_NONE_IN_DEVICE \"name\":\"No Fault in Device\" \"faults\":[] \"serialNumber\":\"04000001\"}]")]
    [InlineData("+OK []")]
    [InlineData("+OK \"value\":[]")]
    public void No_faults_is_ok(string reply)
    {
        var list = TesiraDanteParser.FaultList(reply);

        list.Should().NotBeNull();
        list!.Level.Should().Be("ok");
        list.Faults.Should().BeEmpty();
        TesiraDanteValues.FaultsJson(list).Should().Be("[]");
        TesiraDanteValues.Issues(list).Should().BeEmpty();
    }

    [Theory]
    [InlineData("+OK [[2 \"Minor Fault in Device\" [] \"04000001\"]]", "warning", "Minor Fault in Device")]
    [InlineData("+OK [[3 \"Major Fault in System\" [] \"04000001\"]]", "fault", "Major Fault in System")]
    [InlineData("+OK \"value\":[{\"id\":INDICATOR_MINOR_IN_DEVICE \"name\":\"Minor Fault in Device\" \"faults\":[] \"serialNumber\":\"04000001\"}]",
        "warning", "Minor Fault in Device")]
    public void An_entry_in_fault_with_nothing_listed_under_it_still_says_what_is_wrong(string reply, string level, string name)
    {
        var list = TesiraDanteParser.FaultList(reply)!;

        list.Level.Should().Be(level);
        list.Faults.Select(f => f.Name).Should().Equal(name);
    }

    [Fact]
    public void A_numbered_indicator_reads_its_level_from_its_name()
    {
        TesiraDanteParser.LevelOfName("No Fault in Device").Should().Be("ok");
        TesiraDanteParser.LevelOfName("Minor Fault in System").Should().Be("warning");
        TesiraDanteParser.LevelOfName("Major Fault in Device").Should().Be("fault");
        TesiraDanteParser.LevelOfName("Critical Fault in Device").Should().Be("fault");
        TesiraDanteParser.LevelOfName("Something new").Should().Be("unknown");
    }

    [Theory]
    [InlineData(DanteInfo)]
    [InlineData(VerboseDanteInfo)]
    [InlineData(BracedDanteInfo)]
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
