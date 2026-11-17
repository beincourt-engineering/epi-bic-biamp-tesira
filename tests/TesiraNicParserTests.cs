using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Tests;

/// <summary>
/// Every network interface the DSP reports: control from networkStatus, Dante from danteInfo, the VoIP card from
/// its control/status block's network attribute. The shapes are a TesiraFORTÉ's on firmware 5.7; addresses,
/// MACs and names are made up.
/// </summary>
public class TesiraNicParserTests
{
    private const string VerboseNetworkStatus =
        "+OK \"value\":{\"schemaVersion\":2 \"hostname\":\"Dsp\" \"defaultGatewayStatus\":\"0.0.0.0\" " +
        "\"networkInterfaceStatusWithName\":[{\"interfaceId\":\"control\" \"networkInterfaceStatus\":{\"macAddress\":\"00:90:5e:00:00:01\" " +
        "\"linkStatus\":LINK_1_GB \"addressSource\":DHCP \"ip\":\"192.0.2.10\" \"netmask\":\"255.255.255.0\" " +
        "\"dhcpLeaseObtainedDate\":\"Fri Oct 16 13:43:40 UTC 2026\" \"dhcpLeaseExpiresDate\":\"Sat Oct 17 13:43:40 UTC 2026\" \"gateway\":\"192.0.2.1\"}}] " +
        "\"dnsStatus\":{\"primaryDNSServer\":\"192.0.2.1\" \"secondaryDNSServer\":\"\" \"domainName\":\"\"} \"mDNSEnabled\":true}";

    private const string NetworkStatus =
        "+OK [2 \"Dsp\" \"0.0.0.0\" [[\"control\" [\"00:90:5e:00:00:01\" 4 2 \"192.0.2.10\" \"255.255.255.0\" " +
        "\"Fri Oct 16 13:43:40 UTC 2026\" \"Sat Oct 17 13:43:40 UTC 2026\" \"192.0.2.1\"]]] [\"192.0.2.1\" \"\" \"\"] true false false 1 false true false 0 0]";

    private const string VerboseDanteInfo =
        "+OK \"danteInfo\":{\"revs\":{\"danteAPIVersion_major\":4} \"netCfg\":{\"hostname\":\"Dsp-DAN\" " +
        "\"primaryInterface\":{\"autoConfigure\":true \"ipAddress\":\"192.0.2.11\" \"netMask\":\"255.255.255.0\" \"DNSServer\":\"192.0.2.1\" \"defaultGateway\":\"192.0.2.1\"} " +
        "\"secondaryInterface\":{\"autoConfigure\":true \"ipAddress\":\"192.0.2.11\" \"netMask\":\"255.255.255.0\" \"DNSServer\":\"192.0.2.1\" \"defaultGateway\":\"192.0.2.1\"}} " +
        "\"allNetStatus\":{\"primaryStatus\":{\"macAddress\":\"00:1d:c1:00:00:01\" \"linkSpeedInMbS\":1000} \"secondaryStatus\":{\"macAddress\":\"\" \"linkSpeedInMbS\":0}} " +
        "\"preferredDanteNetworkClockSource\":false \"networkClockSlaveOnly\":false \"networkLatencyInMicroseconds\":2000 \"chInfos\":[]}";

    private const string DanteInfo =
        "+OK [[4 4 1 1 3 81 1 3 0 11] [\"Dsp-DAN\" [true \"192.0.2.11\" \"255.255.255.0\" \"192.0.2.1\" \"192.0.2.1\"] " +
        "[true \"192.0.2.11\" \"255.255.255.0\" \"192.0.2.1\" \"192.0.2.1\"]] [[\"00:1d:c1:00:00:01\" 1000] [\"\" 0]] false false 2000 []]";

    // dnsPrimary -1073741631 is 0xC00000C1 as an unsigned number: bytes C1 00 00 C0, lowest first = 193.0.0.192
    private const string VerboseVoipNetwork =
        "+OK \"value\":{\"general\":{\"mac\":\"78:45:01:00:00:02\" \"dhcpEnable\":true \"dhcpSrvAddr\":0 \"addr\":0 \"netmask\":0 \"gateway\":0 " +
        "\"dnsPrimary\":-1073741631 \"dnsSecondary\":0 \"domain\":\"\" \"detectDuplicatedIP\":true \"enableHttp\":false \"enableTelnet\":false \"enableHttps\":false} " +
        "\"vlan\":{\"vlanEnable\":false \"vlanId\":1} \"ethnet\":{\"ethSpeed\":VOIP_ETH_AUTO \"ethDuplex\":VOIP_ETH_FULL \"padShortFrame\":true " +
        "\"acceptShortFrame\":true \"ignoreUnsolicitedArp\":false}}";

    private const string VoipNetwork =
        "+OK [[\"78:45:01:00:00:02\" true 0 0 0 0 -1073741631 0 \"\" true false false false] [false 1] [3 2 true true false] " +
        "[3 \"\" false 1024 5 \"\"] [1 \"\" 150] [3 0 false \"\" \"\" \"\" false false [] []]]";

    [Theory]
    [InlineData(VerboseNetworkStatus)]
    [InlineData(NetworkStatus)]
    public void Reads_the_control_port_either_way(string reply)
    {
        var nics = TesiraNicParser.ControlNics(reply);

        nics.Should().ContainSingle();
        var c = nics[0];
        c.Name.Should().Be("control");
        c.Mac.Should().Be("00:90:5e:00:00:01");
        c.Link.Should().Be("1 Gb");
        c.LinkMbps.Should().Be(1000);
        c.LinkUp.Should().BeTrue();
        c.Addressing.Should().Be("DHCP");
        c.Ip.Should().Be("192.0.2.10");
        c.Netmask.Should().Be("255.255.255.0");
        c.Gateway.Should().Be("192.0.2.1");
        c.Dns.Should().Equal("192.0.2.1");
        c.DhcpLeaseObtained.Should().Be("Fri Oct 16 13:43:40 UTC 2026");
        c.DhcpLeaseExpires.Should().Be("Sat Oct 17 13:43:40 UTC 2026");
        TesiraNicParser.Hostname(reply).Should().Be("Dsp");
    }

    [Fact]
    public void An_enum_code_never_seen_on_a_unit_is_kept_as_its_code()
    {
        var reply = NetworkStatus.Replace("\"00:90:5e:00:00:01\" 4 2", "\"00:90:5e:00:00:01\" 7 1");
        var c = TesiraNicParser.ControlNics(reply)[0];

        c.Link.Should().Be("code 7");
        c.LinkUp.Should().BeNull("an unknown code says nothing about the link");
        c.LinkMbps.Should().BeNull();
        c.Addressing.Should().Be("code 1");
    }

    [Fact]
    public void Reads_a_static_port_and_a_link_that_is_down_when_verbose()
    {
        var reply = VerboseNetworkStatus.Replace("LINK_1_GB", "LINK_DOWN").Replace("DHCP", "STATIC");
        var c = TesiraNicParser.ControlNics(reply)[0];

        c.LinkUp.Should().BeFalse();
        c.Link.Should().Be("down");
        c.Addressing.Should().Be("static");
    }

    [Theory]
    [InlineData(VerboseDanteInfo)]
    [InlineData(DanteInfo)]
    public void Reads_both_dante_ports_either_way(string reply)
    {
        var nics = TesiraNicParser.DanteNics(reply);

        nics.Should().HaveCount(2);
        var p = nics[0];
        p.Name.Should().Be("dante primary");
        p.Hostname.Should().Be("Dsp-DAN");
        p.Mac.Should().Be("00:1d:c1:00:00:01");
        p.LinkUp.Should().BeTrue();
        p.LinkMbps.Should().Be(1000);
        p.Link.Should().Be("1000 Mb/s");
        p.Addressing.Should().Be("DHCP");
        p.Ip.Should().Be("192.0.2.11");
        p.Netmask.Should().Be("255.255.255.0");
        p.Gateway.Should().Be("192.0.2.1");
        p.Dns.Should().Equal("192.0.2.1");

        var s = nics[1];
        s.Name.Should().Be("dante secondary");
        s.Mac.Should().BeNull("an unconnected secondary sends no MAC");
        s.LinkUp.Should().BeFalse();
        s.Link.Should().Be("down");
        s.LinkMbps.Should().BeNull();
    }

    [Theory]
    [InlineData(VerboseVoipNetwork)]
    [InlineData(VoipNetwork)]
    public void Reads_the_voip_card_either_way(string reply)
    {
        var v = TesiraNicParser.VoipNic(reply, "voip")!;

        v.Name.Should().Be("voip");
        v.Mac.Should().Be("78:45:01:00:00:02");
        v.Addressing.Should().Be("DHCP");
        v.Ip.Should().BeNull("0 is no address: a card without a link has none");
        v.Netmask.Should().BeNull();
        v.Gateway.Should().BeNull();
        v.Dns.Should().Equal("193.0.0.192");
        v.Vlan.Should().BeNull("the VLAN is off");
        v.Ethernet.Should().Be("auto, full duplex");
    }

    [Fact]
    public void Reads_a_voip_card_s_address_and_vlan()
    {
        // 192.0.2.20 lowest byte first is 0x140200C0 = 335675584
        var reply = VoipNetwork.Replace("true 0 0 0 0 -1073741631", "false 0 335675584 0 0 -1073741631").Replace("[false 1]", "[true 20]");
        var v = TesiraNicParser.VoipNic(reply, "voip")!;

        v.Addressing.Should().Be("static");
        v.Ip.Should().Be("192.0.2.20");
        v.Vlan.Should().Be(20);
    }

    [Theory]
    [InlineData("+OK \"value\":false")]
    [InlineData("+OK [[4 4 1 1 3 81 1 3 0 11] [\"Dsp-DAN\"]]")]
    [InlineData("-ERR address not found: {\"deviceId\":0 \"classCode\":0 \"instanceNum\":0}")]
    [InlineData("")]
    public void Another_reply_is_no_voip_card(string reply)
    {
        TesiraNicParser.VoipNic(reply, "voip").Should().BeNull();
    }

    [Theory]
    [InlineData("+OK \"value\":\"04223456\"")]
    [InlineData("-ERR ATTRIBUTE_NOT_SUPPORTED")]
    [InlineData("")]
    public void Another_reply_is_no_control_or_dante_port(string reply)
    {
        TesiraNicParser.ControlNics(reply).Should().BeEmpty();
        TesiraNicParser.DanteNics(reply).Should().BeEmpty();
    }

    [Fact]
    public void The_network_feedback_lists_every_interface_as_json()
    {
        var nics = TesiraNicParser.ControlNics(NetworkStatus);
        nics.AddRange(TesiraNicParser.DanteNics(DanteInfo));
        nics.Add(TesiraNicParser.VoipNic(VoipNetwork, "voip")!);

        var json = TesiraNicParser.NetworkJson("Dsp", nics);
        var o = JObject.Parse(json);

        json.Length.Should().BeLessThan(TesiraNicParser.MaxValueChars);
        o["hostname"]!.Value<string>().Should().Be("Dsp");
        var names = ((JArray)o["interfaces"]!).Select(i => i["name"]!.Value<string>());
        names.Should().Equal("control", "dante primary", "dante secondary", "voip");
        o["interfaces"]![0]!["linkMbps"]!.Value<int>().Should().Be(1000);
        o["interfaces"]![2]!["mac"].Should().BeNull("blank values are left out");
        o["interfaces"]![3]!["ethernet"]!.Value<string>().Should().Be("auto, full duplex");
    }

    [Fact]
    public void A_voip_card_s_capital_mac_is_read_in_lowercase()
    {
        // a VoIP card sends its MAC in capitals; networkStatus and danteInfo send theirs in lowercase
        var reply = VoipNetwork.Replace("\"78:45:01:00:00:02\"", "\"78:45:01:0A:0B:CD\"");

        TesiraNicParser.VoipNic(reply, "voip")!.Mac.Should().Be("78:45:01:0a:0b:cd");
    }

    [Fact]
    public void The_mac_list_is_lowercase_and_drops_a_repeat_in_other_case()
    {
        TesiraDeviceInfoParser.MacList(new[] { "00:90:5E:00:00:01" }, "00:1d:c1:00:00:01", "00:90:5e:00:00:01", new[] { "78:45:01:0A:0B:CD" })
            .Should().Equal("00:90:5e:00:00:01", "00:1d:c1:00:00:01", "78:45:01:0a:0b:cd");
    }

    [Fact]
    public void The_mac_list_takes_the_voip_card_after_dante()
    {
        TesiraDeviceInfoParser.MacList(new[] { "00:90:5e:00:00:01" }, "00:1d:c1:00:00:01", "", new[] { "78:45:01:00:00:02" })
            .Should().Equal("00:90:5e:00:00:01", "00:1d:c1:00:00:01", "78:45:01:00:00:02");
        TesiraDeviceInfoParser.MacList(new[] { "00:90:5e:00:00:01" }, null, null, new[] { "78:45:01:00:00:02" })
            .Should().Equal("00:90:5e:00:00:01", "78:45:01:00:00:02");
    }
}
