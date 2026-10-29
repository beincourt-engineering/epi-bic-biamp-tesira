using FluentAssertions;
using Xunit;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Tests;

/// <summary>
/// The device info component reads its replies through this parser. Every reply to a get starts
/// <c>+OK "value":</c>, so reading the first quoted string returns the word "value".
/// </summary>
public class TesiraDeviceInfoParserTests
{
    private const string NetworkStatus =
        "+OK \"value\":{\"schemaVersion\":2 \"hostname\":\"TesiraForte\" \"defaultGatewayStatus\":\"192.0.2.1\" " +
        "\"networkInterfaceStatusWithName\":[{\"interfaceId\":\"control\" \"networkInterfaceStatus\":{" +
        "\"macAddress\":\"00:90:5e:13:3b:27\" \"linkStatus\":LINK_1_GB \"addressSource\":STATIC \"ip\":\"192.0.2.10\" " +
        "\"netmask\":\"255.255.255.0\" \"dhcpLeaseObtainedDate\":\"\" \"dhcpLeaseExpiresDate\":\"\" \"gateway\":\"192.0.2.1\"}}] " +
        "\"dnsStatus\":{\"primaryDNSServer\":\"\" \"secondaryDNSServer\":\"\" \"domainName\":\"\"} \"mDNSEnabled\":true}";

    [Theory]
    [InlineData("+OK \"value\":\"4.7.2.24086\"", "4.7.2.24086")]
    [InlineData("+OK \"value\":\"04223456\"", "04223456")]
    [InlineData("+OK \"value\":\"04223456\"\r", "04223456")]
    [InlineData("+OK \"value\":-12.000000", "-12.000000")]
    public void Reads_the_value_after_its_key(string reply, string expected)
    {
        TesiraDeviceInfoParser.Value(reply).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+OK")]
    [InlineData("+OK \"value\":\"\"")]
    [InlineData("-ERR address not found: {\"deviceId\":0 \"classCode\":0 \"instanceNum\":0}")]
    public void A_reply_without_a_value_is_none(string? reply)
    {
        TesiraDeviceInfoParser.Value(reply!).Should().BeNull();
    }

    [Fact]
    public void Reads_network_fields_by_name_not_position()
    {
        TesiraDeviceInfoParser.Field(NetworkStatus, "hostname").Should().Be("TesiraForte");
        TesiraDeviceInfoParser.Field(NetworkStatus, "macAddress").Should().Be("00:90:5e:13:3b:27");
        TesiraDeviceInfoParser.Field(NetworkStatus, "ip").Should().Be("192.0.2.10");
    }

    [Fact]
    public void Ip_is_not_confused_with_gateway_or_dns()
    {
        // "defaultGatewayStatus" and "gateway" hold addresses too; only "ip" is the device's own.
        TesiraDeviceInfoParser.Field(NetworkStatus, "ip").Should().NotBe("192.0.2.1");
    }

    [Fact]
    public void An_empty_or_missing_field_is_none()
    {
        TesiraDeviceInfoParser.Field(NetworkStatus, "domainName").Should().BeNull();
        TesiraDeviceInfoParser.Field(NetworkStatus, "serialNumber").Should().BeNull();
        TesiraDeviceInfoParser.Field("", "hostname").Should().BeNull();
    }
}
