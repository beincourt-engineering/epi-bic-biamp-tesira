using FluentAssertions;
using Xunit;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Tests;

/// <summary>
/// The readings a timeline draws for a DSP. The cardStat shapes are a TesiraFORTÉ VT's on firmware 5.7; the numbers
/// are made up.
/// </summary>
public class TesiraTimelineValuesTests
{
    private const string VerboseCardStat =
        "+OK \"value\":{\"networkStat\":{\"rxFrames\":1200 \"txFrames\":800 \"elapsedTime\":\"028:22:32:15\" " +
        "\"timeInSeconds\":1794941263 \"timeInSecondsUTC\":1794970063} \"firmwareInfo\":{\"bootloadId\":\"01.01.00\" " +
        "\"ApploadId\":\"01.17.00.006\"} \"callStat\":[]}";

    private const string CardStat =
        "+OK [[1200 800 \"028:22:32:15\" 1794941263 1794970063] [\"01.01.00\" \"01.17.00.006\"] []]";

    [Theory]
    [InlineData(VerboseCardStat)]
    [InlineData(CardStat)]
    public void Reads_a_voip_card_s_counters_and_firmware_either_way(string reply)
    {
        var stat = TesiraTimelineValues.CardStat(reply)!;

        stat.RxFrames.Should().Be(1200);
        stat.TxFrames.Should().Be(800);
        stat.Firmware.Should().Be("01.17.00.006");
    }

    [Theory]
    [InlineData("+OK \"value\":{\"general\":{\"mac\":\"78:45:01:00:00:02\" \"dhcpEnable\":true}}")]
    [InlineData("+OK [[\"78:45:01:00:00:02\" true 0 0 0 0 0 0 \"\" true false false false] [false 1]]")]
    [InlineData("+OK \"value\":false")]
    [InlineData("-ERR address not found: {\"deviceId\":0 \"classCode\":0 \"instanceNum\":0}")]
    [InlineData("")]
    public void Another_reply_is_no_card_stat(string reply)
    {
        TesiraTimelineValues.CardStat(reply).Should().BeNull();
    }

    [Fact]
    public void Frames_a_minute_come_from_two_reads()
    {
        TesiraTimelineValues.FramesPerMinute(1000, 0, 4000, 60000).Should().Be(3000);
        TesiraTimelineValues.FramesPerMinute(1000, 0, 1500, 30000).Should().Be(1000, "500 frames in half a minute");
    }

    [Fact]
    public void Frames_a_minute_are_unread_on_the_first_read_a_restart_or_a_read_too_soon()
    {
        TesiraTimelineValues.FramesPerMinute(null, null, 4000, 60000).Should().BeNull();
        TesiraTimelineValues.FramesPerMinute(4000, 0, 100, 60000).Should().BeNull("the counter went back: the card restarted");
        TesiraTimelineValues.FramesPerMinute(4000, 0, 4100, 500).Should().BeNull();
    }

    [Fact]
    public void Finds_a_port_by_name_in_any_case()
    {
        var nics = new List<TesiraNic> { new TesiraNic { Name = "control" }, new TesiraNic { Name = "dante primary" } };

        TesiraTimelineValues.Port(nics, "Dante Primary").Should().BeSameAs(nics[1]);
        TesiraTimelineValues.Port(nics, "voip").Should().BeNull();
        TesiraTimelineValues.Port(null!, "control").Should().BeNull();
    }

    [Fact]
    public void A_reading_not_read_is_empty_text()
    {
        TesiraTimelineValues.Text((bool?)null).Should().BeEmpty();
        TesiraTimelineValues.Text(true).Should().Be("true");
        TesiraTimelineValues.Text(false).Should().Be("false");
        TesiraTimelineValues.Text((long?)null).Should().BeEmpty();
        TesiraTimelineValues.Text(1000L).Should().Be("1000");
    }

    [Fact]
    public void The_voip_card_s_firmware_goes_out_with_its_interface()
    {
        var nic = TesiraNicParser.VoipNic(
            "+OK [[\"78:45:01:00:00:02\" true 0 0 0 0 0 0 \"\" true false false false] [false 1] [3 2 true true false]]", "voip")!;
        nic.Firmware = "01.17.00.006";
        nic.FramesPerMin = 3000;

        var json = TesiraNicParser.NetworkJson("Dsp", new List<TesiraNic> { nic });
        json.Should().Contain("\"firmware\":\"01.17.00.006\"");
        json.Should().Contain("\"framesPerMin\":3000");
    }
}
