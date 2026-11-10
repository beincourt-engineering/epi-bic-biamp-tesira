using FluentAssertions;
using Xunit;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Tests;

/// <summary>
/// Source-level guards for what the DSP lists in IHasFeedback.Feedbacks. Monitoring consumers read
/// that list as the device's state, so a value that changes on every line received, or with the
/// audio, does not belong on it. The plugin needs a processor to run, so these pin the source.
/// </summary>
public class FeedbackListSourceTests
{
    private static string Source(string file)
    {
        var path = Path.Combine(AssemblyFixture.SourceDirectory, file);
        File.Exists(path).Should().BeTrue($"the source should be at {path}");
        return File.ReadAllText(path);
    }

    [Theory]
    [InlineData("TesiraDsp.cs", "Feedbacks.Add(CommandPassthruFeedback)")]
    [InlineData("TesiraDspDeviceInfo.cs", "Feedbacks.Add(Parent.CommandPassthruFeedback)")]
    public void The_Command_Echo_Is_Not_Listed(string file, string add)
    {
        // It changes on every line the DSP sends.
        Source(file).Should().NotContain(add);
    }

    [Theory]
    [InlineData("TesiraDsp.cs", "CommandPassthruFeedback.LinkInputSig(")]
    [InlineData("TesiraDspDeviceInfo.cs", "Parent.CommandPassthruFeedback.LinkInputSig(")]
    public void The_Command_Echo_Still_Reaches_The_Bridge(string file, string link)
    {
        Source(file).Should().Contain(link);
    }

    [Fact]
    public void Meter_Values_Are_Not_Added_To_The_Dsp_List()
    {
        // A meter changes with the audio.
        var source = Source("TesiraDspMeter.cs");

        source.Should().NotContain("parent.Feedbacks");
        source.Should().NotContain("Parent.Feedbacks");
    }

    [Fact]
    public void Meter_Values_Still_Reach_The_Bridge()
    {
        Source("TesiraDsp.cs").Should().Contain("meter.MeterFeedback.LinkInputSig(");
        Source("TesiraDspMeter.cs").Should().Contain("MeterFeedback.LinkInputSig(");
    }
}
