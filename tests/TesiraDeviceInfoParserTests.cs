using FluentAssertions;
using Xunit;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Tests;

/// <summary>
/// The device info component reads its replies through this parser. The plugin runs the session
/// non-verbose, so replies carry values without names; verbose replies are read too. Replies are
/// matched to commands by order, so a reader must refuse another attribute's reply.
/// </summary>
public class TesiraDeviceInfoParserTests
{
    private const string VerboseNetworkStatus =
        "+OK \"value\":{\"schemaVersion\":2 \"hostname\":\"TesiraForte\" \"defaultGatewayStatus\":\"192.0.2.1\" " +
        "\"networkInterfaceStatusWithName\":[{\"interfaceId\":\"control\" \"networkInterfaceStatus\":{" +
        "\"macAddress\":\"00:90:5e:13:3b:27\" \"linkStatus\":LINK_1_GB \"addressSource\":STATIC \"ip\":\"192.0.2.10\" " +
        "\"netmask\":\"255.255.255.0\" \"dhcpLeaseObtainedDate\":\"\" \"dhcpLeaseExpiresDate\":\"\" \"gateway\":\"192.0.2.1\"}}] " +
        "\"dnsStatus\":{\"primaryDNSServer\":\"\" \"secondaryDNSServer\":\"\" \"domainName\":\"\"} \"mDNSEnabled\":true}";

    // The same reply with the names left out, as the non-verbose session sends it: hostname,
    // gateway, then the interface's name, MAC and address - the order the original positional
    // parser read (strings 0, 3 and 4).
    private const string NetworkStatus =
        "+OK {2 \"TesiraForte\" \"192.0.2.1\" [{\"control\" {\"00:90:5e:13:3b:27\" LINK_1_GB STATIC \"192.0.2.10\" " +
        "\"255.255.255.0\" \"\" \"\" \"192.0.2.1\"}}] {\"\" \"\" \"\"} true}";

    private const string DiscoveredServers =
        "+OK [[\"192.0.2.10\" \"TesiraForte\" false 1 11] [\"192.0.2.11\" \"TesiraForte2\" false 1 11]]";

    [Theory]
    [InlineData("+OK \"4.7.2.24086\"", "4.7.2.24086")]
    [InlineData("+OK \"5.7.1.4\"\r", "5.7.1.4")]
    [InlineData("+OK \"value\":\"4.7.2.24086\"", "4.7.2.24086")]
    public void Reads_the_firmware_version_in_either_style(string reply, string expected)
    {
        TesiraDeviceInfoParser.Firmware(reply).Should().Be(expected);
    }

    [Theory]
    [InlineData("+OK \"04223456\"", "04223456")]
    [InlineData("+OK \"04223456\"\r", "04223456")]
    [InlineData("+OK \"value\":\"04223456\"", "04223456")]
    public void Reads_the_serial_number_in_either_style(string reply, string expected)
    {
        TesiraDeviceInfoParser.Serial(reply).Should().Be(expected);
    }

    [Theory]
    [InlineData(VerboseNetworkStatus)]
    [InlineData(NetworkStatus)]
    public void Reads_host_name_mac_and_address_in_either_style(string reply)
    {
        var status = TesiraDeviceInfoParser.NetworkStatus(reply);

        status.Should().NotBeNull();
        status!.Hostname.Should().Be("TesiraForte");
        status.MacAddress.Should().Be("00:90:5e:13:3b:27");
        status.IpAddress.Should().Be("192.0.2.10");
    }

    [Fact]
    public void Address_is_not_confused_with_the_gateway()
    {
        TesiraDeviceInfoParser.NetworkStatus(NetworkStatus)!.IpAddress.Should().NotBe("192.0.2.1");
        TesiraDeviceInfoParser.NetworkStatus(VerboseNetworkStatus)!.IpAddress.Should().NotBe("192.0.2.1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+OK")]
    [InlineData("+OK \"\"")]
    [InlineData("+OK \"value\":\"\"")]
    [InlineData("-ERR address not found: {\"deviceId\":0 \"classCode\":0 \"instanceNum\":0}")]
    public void A_reply_without_a_value_is_none(string? reply)
    {
        TesiraDeviceInfoParser.Firmware(reply!).Should().BeNull();
        TesiraDeviceInfoParser.Serial(reply!).Should().BeNull();
        TesiraDeviceInfoParser.NetworkStatus(reply!).Should().BeNull();
    }

    // A late reply shifts every reply after it onto the next command: a version reply has been
    // seen arriving as the discoveredServers answer, and the discoveredServers list as a level's.

    [Theory]
    [InlineData("+OK \"5.7.1.4\"")]
    [InlineData(NetworkStatus)]
    [InlineData(DiscoveredServers)]
    [InlineData("+OK -12.000000")]
    [InlineData("+OK true")]
    [InlineData("+OK \"BIC-DSP1\"")]
    [InlineData("+OK \"Preset\"")]
    public void Another_attribute_s_reply_is_not_a_serial_number(string reply)
    {
        TesiraDeviceInfoParser.Serial(reply).Should().BeNull();
    }

    [Theory]
    [InlineData("+OK \"04223456\"")]
    [InlineData(NetworkStatus)]
    [InlineData(DiscoveredServers)]
    [InlineData("+OK -12.000000")]
    [InlineData("+OK \"192.0.2.10\"")]
    public void Another_attribute_s_reply_is_not_a_version(string reply)
    {
        TesiraDeviceInfoParser.Firmware(reply).Should().BeNull();
    }

    [Theory]
    [InlineData("+OK \"5.7.1.4\"")]
    [InlineData("+OK \"04223456\"")]
    [InlineData(DiscoveredServers)]
    [InlineData("+OK -12.000000")]
    public void Another_attribute_s_reply_is_not_a_network_status(string reply)
    {
        TesiraDeviceInfoParser.NetworkStatus(reply).Should().BeNull();
    }

    // networkStatus as a DSP on firmware 5.7 sends it non-verbose (address and MAC made up): a list,
    // the interfaces as [name [mac link source ip ...]].
    private const string DspNetworkStatus =
        "+OK [2 \"Dsp\" \"0.0.0.0\" [[\"control\" [\"00:90:5e:13:3b:27\" 4 2 \"192.0.2.10\" \"255.255.255.0\" " +
        "\"\" \"\" \"192.0.2.1\"]]] [\"192.0.2.1\" \"\" \"\"] true false false 1 false true false 0 0]";

    // A server with two network ports lists both, control first (made up).
    private const string TwoPortNetworkStatus =
        "+OK [2 \"Server\" \"0.0.0.0\" [[\"control\" [\"00:90:5e:00:00:01\" 4 2 \"192.0.2.10\" \"255.255.255.0\"]] " +
        "[\"media\" [\"00:90:5e:00:00:02\" 4 2 \"198.51.100.10\" \"255.255.255.0\"]]] [\"192.0.2.1\" \"\" \"\"] true]";

    private const string TwoPortVerboseNetworkStatus =
        "+OK \"value\":{\"schemaVersion\":2 \"hostname\":\"Server\" \"networkInterfaceStatusWithName\":[" +
        "{\"interfaceId\":\"control\" \"networkInterfaceStatus\":{\"macAddress\":\"00:90:5e:00:00:01\" \"ip\":\"192.0.2.10\"}} " +
        "{\"interfaceId\":\"media\" \"networkInterfaceStatus\":{\"macAddress\":\"00:90:5e:00:00:02\" \"ip\":\"198.51.100.10\"}}]}";

    [Fact]
    public void Lists_the_one_interface_of_a_dsp_with_one_port()
    {
        foreach (var reply in new[] { DspNetworkStatus, NetworkStatus, VerboseNetworkStatus })
        {
            var status = TesiraDeviceInfoParser.NetworkStatus(reply)!;
            status.MacAddress.Should().Be("00:90:5e:13:3b:27");
            status.MacAddresses.Should().Equal("00:90:5e:13:3b:27");
        }

        TesiraDeviceInfoParser.NetworkStatus(DspNetworkStatus)!.IpAddress.Should().Be("192.0.2.10");
        TesiraDeviceInfoParser.NetworkStatus(DspNetworkStatus)!.Hostname.Should().Be("Dsp");
    }

    [Theory]
    [InlineData(TwoPortNetworkStatus)]
    [InlineData(TwoPortVerboseNetworkStatus)]
    public void Lists_every_interface_control_first(string reply)
    {
        var status = TesiraDeviceInfoParser.NetworkStatus(reply)!;
        status.MacAddress.Should().Be("00:90:5e:00:00:01");
        status.MacAddresses.Should().Equal("00:90:5e:00:00:01", "00:90:5e:00:00:02");
        status.IpAddress.Should().Be("192.0.2.10");
    }

    [Fact]
    public void The_mac_list_puts_the_interfaces_first_then_dante()
    {
        TesiraDeviceInfoParser.MacList(new[] { "78:45:01:00:00:01" }, "00:1d:c1:00:00:01", "")
            .Should().Equal("78:45:01:00:00:01", "00:1d:c1:00:00:01");
        TesiraDeviceInfoParser.MacList(new[] { "00:90:5e:00:00:01", "00:90:5e:00:00:02" }, "00:1d:c1:00:00:01", "00:1d:c1:00:00:02")
            .Should().Equal("00:90:5e:00:00:01", "00:90:5e:00:00:02", "00:1d:c1:00:00:01", "00:1d:c1:00:00:02");
    }

    [Fact]
    public void The_mac_list_leaves_out_blanks_repeats_and_non_macs()
    {
        TesiraDeviceInfoParser.MacList(new[] { "00:90:5e:00:00:01", " ", "00:90:5E:00:00:01" }, " 00:1d:c1:00:00:01 ", "unknown")
            .Should().Equal("00:90:5e:00:00:01", "00:1d:c1:00:00:01");
    }

    [Fact]
    public void One_mac_or_none_is_no_list()
    {
        // the single MAC already says it
        TesiraDeviceInfoParser.MacList(new[] { "00:90:5e:00:00:01" }, null, "").Should().BeNull();
        TesiraDeviceInfoParser.MacList(null, null, null).Should().BeNull();
        TesiraDeviceInfoParser.MacList(new string[0], "00:1d:c1:00:00:01", null).Should().BeNull();
    }

    [Fact]
    public void The_mac_list_stops_at_eight()
    {
        var many = Enumerable.Range(1, 12).Select(i => $"00:90:5e:00:00:{i:x2}");
        TesiraDeviceInfoParser.MacList(many, null, null).Should().HaveCount(TesiraDeviceInfoParser.MaxMacAddresses);
    }

    // deviceInfo returns deviceModel, deviceRevision, serialNumber, firmwareVersion and ipAddress,
    // in that order (Tesira Text Protocol DEVICE attribute table). No example reply is published, so
    // these are built from that order in both session styles.
    private const string VerboseDeviceInfo =
        "+OK \"value\":{\"deviceModel\":\"TesiraFORTE X 1600\" \"deviceRevision\":\"1\" " +
        "\"serialNumber\":\"04223456\" \"firmwareVersion\":\"5.7.1.4\" \"ipAddress\":\"192.0.2.10\"}";

    private const string DeviceInfo =
        "+OK {\"TesiraFORTE X 1600\" \"1\" \"04223456\" \"5.7.1.4\" \"192.0.2.10\"}";

    [Theory]
    [InlineData(VerboseDeviceInfo)]
    [InlineData(DeviceInfo)]
    [InlineData(DeviceInfo + "\r")]
    public void Reads_the_model_in_either_style(string reply)
    {
        TesiraDeviceInfoParser.Model(reply, null).Should().Be("TesiraFORTE X 1600");
        TesiraDeviceInfoParser.Model(reply, "04223456").Should().Be("TesiraFORTE X 1600");
    }

    [Theory]
    [InlineData("+OK \"value\":{\"deviceModel\":TESIRAFORTE_X_1600 \"serialNumber\":\"04223456\"}")]
    [InlineData("+OK {TESIRAFORTE_X_1600 \"1\" \"04223456\" \"5.7.1.4\" \"192.0.2.10\"}")]
    public void Reads_a_model_sent_as_a_bare_word(string reply)
    {
        TesiraDeviceInfoParser.Model(reply, "04223456").Should().Be("TESIRAFORTE_X_1600");
    }

    // What a TesiraFORTÉ on firmware 5.7 sends (shapes from a unit; serial and address made up):
    // verbose, the fields sit under "info", not "value"; non-verbose, they come as a list.
    private const string DspVerboseDeviceInfo =
        "+OK \"info\":{\"deviceModel\":\"TesiraFORTÉ DAN VT\" \"deviceRevision\":\"Rev. A\" " +
        "\"serialNumber\":\"04223456\" \"firmwareVersion\":\"5.7.0.12\" \"ipAddress\":\"192.0.2.10\"}";

    private const string DspDeviceInfo =
        "+OK [\"TesiraFORTÉ DAN VT\" \"Rev. A\" \"04223456\" \"5.7.0.12\" \"192.0.2.10\"]";

    [Theory]
    [InlineData(DspVerboseDeviceInfo)]
    [InlineData(DspDeviceInfo)]
    [InlineData(DspDeviceInfo + "\r")]
    public void Reads_the_model_as_a_dsp_sends_it(string reply)
    {
        TesiraDeviceInfoParser.Model(reply, null).Should().Be("TesiraFORTÉ DAN VT");
        TesiraDeviceInfoParser.Model(reply, "04223456").Should().Be("TesiraFORTÉ DAN VT");
        TesiraDeviceInfoParser.Model(reply, "09999999").Should().BeNull("a reply without the known serial is another DSP's or a misread");
    }

    [Fact]
    public void Puts_back_an_accent_that_arrived_one_byte_per_character()
    {
        // É is C3 89 in UTF-8; a session that reads one byte per character sees "Ã" and U+0089.
        var garbled = DspDeviceInfo.Replace("É", "Ã\u0089");
        TesiraDeviceInfoParser.Model(garbled, "04223456").Should().Be("TesiraFORTÉ DAN VT");
    }

    [Theory]
    [InlineData("TesiraFORTÉ DAN VT", "TesiraFORTÉ DAN VT")]
    [InlineData("TesiraFORTÃ\u0089 DAN VT", "TesiraFORTÉ DAN VT")]
    [InlineData("TesiraFORTE X 1600", "TesiraFORTE X 1600")]
    [InlineData("Café", "Café")] // Latin-1 that isn't UTF-8 read as bytes stays
    [InlineData("", "")]
    public void RepairUtf8_only_changes_utf8_read_as_bytes(string text, string expected)
    {
        TesiraDeviceInfoParser.RepairUtf8(text).Should().Be(expected);
    }

    [Theory]
    // danteInfo, verbose: an object under another key is not a device info reply
    [InlineData("+OK \"danteInfo\":{\"revs\":{\"danteAPIVersion_major\":4} \"netCfg\":{\"hostname\":\"Dsp-DAN\"}}")]
    // danteInfo, non-verbose: a list of lists
    [InlineData("+OK [[4 4 1 1 3 81 1 3 0 11] [\"Dsp-DAN\" [true \"192.0.2.11\"]]]")]
    // networkStatus, non-verbose, as a DSP sends it
    [InlineData("+OK [2 \"Dsp\" \"0.0.0.0\" [[\"control\" [\"00:90:5e:13:3b:27\" 4 2 \"192.0.2.10\"]]] true]")]
    public void A_dsp_s_other_replies_are_not_a_model(string reply)
    {
        TesiraDeviceInfoParser.Model(reply, null).Should().BeNull();
    }

    [Fact]
    public void A_reply_without_the_known_serial_is_not_this_dsp_s_model()
    {
        // A late reply from another command, or a serial misread: never take it as the model.
        TesiraDeviceInfoParser.Model(DeviceInfo, "09999999").Should().BeNull();
    }

    [Theory]
    [InlineData("+OK \"5.7.1.4\"")]
    [InlineData("+OK \"04223456\"")]
    [InlineData("+OK \"TesiraForte\"")]
    [InlineData(NetworkStatus)]
    [InlineData(VerboseNetworkStatus)]
    [InlineData(DiscoveredServers)]
    [InlineData("+OK -12.000000")]
    [InlineData("+OK true")]
    [InlineData("+OK {\"192.0.2.10\" \"1\"}")]
    [InlineData("+OK {\"5.7.1.4\" \"1\"}")]
    [InlineData("+OK {2 \"1\"}")]
    [InlineData("+OK \"value\":{\"deviceRevision\":\"1\"}")]
    [InlineData("-ERR address not found: {\"deviceId\":0 \"classCode\":0 \"instanceNum\":0}")]
    [InlineData("")]
    [InlineData(null)]
    public void Another_attribute_s_reply_is_not_a_model(string? reply)
    {
        TesiraDeviceInfoParser.Model(reply!, null).Should().BeNull();
    }

    [Fact]
    public void A_device_info_reply_is_not_taken_as_another_attribute()
    {
        TesiraDeviceInfoParser.Serial(DeviceInfo).Should().BeNull();
        TesiraDeviceInfoParser.Firmware(DeviceInfo).Should().BeNull();
        TesiraDeviceInfoParser.NetworkStatus(DeviceInfo).Should().BeNull();
        TesiraDeviceInfoParser.NetworkStatus(VerboseDeviceInfo).Should().BeNull();
    }

    [Fact]
    public void Value_reads_either_style()
    {
        TesiraDeviceInfoParser.Value("+OK \"value\":-12.000000").Should().Be("-12.000000");
        TesiraDeviceInfoParser.Value("+OK -12.000000").Should().Be("-12.000000");
        TesiraDeviceInfoParser.Value("+OK \"04223456\"").Should().Be("04223456");
    }

    [Fact]
    public void An_empty_or_missing_field_is_none()
    {
        TesiraDeviceInfoParser.Field(VerboseNetworkStatus, "domainName").Should().BeNull();
        TesiraDeviceInfoParser.Field(VerboseNetworkStatus, "serialNumber").Should().BeNull();
        TesiraDeviceInfoParser.Field("", "hostname").Should().BeNull();
    }
}
