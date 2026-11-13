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
