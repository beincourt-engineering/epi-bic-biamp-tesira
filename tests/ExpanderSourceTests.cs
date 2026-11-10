using FluentAssertions;
using Xunit;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Tests;

/// <summary>
/// Source-level guards for expander status. Each expander reports through a communication monitor
/// whose status follows what the DSP reports, and the tracker's status follows the expanders'. The
/// plugin needs a processor to run, so these pin the source.
/// </summary>
public class ExpanderSourceTests
{
    private static string Source(string file)
    {
        var path = Path.Combine(AssemblyFixture.SourceDirectory, file);
        File.Exists(path).Should().BeTrue($"the source should be at {path}");
        return File.ReadAllText(path);
    }

    private static string Data() => Source(Path.Combine("Expander", "TesiraExpanderData.cs"));

    private static string Tracker() => Source(Path.Combine("Expander", "TesiraExpanderTracker.cs"));

    /// <summary>The text from the opening brace after <paramref name="signature"/> to its match.</summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"'{signature}' should be in the source");

        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }

        throw new InvalidOperationException($"unbalanced braces after '{signature}'");
    }

    private static int Count(string text, string what) => text.Split(what).Length - 1;

    [Fact]
    public void Each_Expander_Is_A_Communication_Monitor()
    {
        var source = Data();

        source.Should().MatchRegex(@"class TesiraExpanderData\s*:[^\{]*ICommunicationMonitor");
        source.Should().Contain("public StatusMonitorBase CommunicationMonitor");
    }

    [Fact]
    public void Expanders_Are_Registered_By_Key()
    {
        Body(Tracker(), "public TesiraExpanderTracker(")
            .Should().Contain("DeviceManager.AddDevice(expander);");
    }

    [Fact]
    public void Online_State_Moves_The_Monitor_Status()
    {
        // StatusMonitorBase.IsOnline is a field: writing it never moved Status or fired anything.
        var source = Data();
        var setOnline = Body(source, "private void SetOnline(bool online)");

        source.Should().NotContain("Monitor.IsOnline =");
        setOnline.Should().Contain("IsOnline.FireUpdate();");
        setOnline.Should().Contain("Monitor.SetStatus(");
    }

    [Fact]
    public void Going_Offline_Is_Reported()
    {
        Body(Data(), "private void SetOffline()").Should().Contain("SetOnline(false);");
    }

    [Fact]
    public void The_Offline_Countdown_Is_One_Shot_And_Restarted_By_Every_Report()
    {
        // It used to repeat and never stop, and the first report returned before publishing.
        var source = Data();
        var setData = Body(source, "public void SetData(string data, string macData)");

        source.Should().Contain("AutoReset = false");
        setData.Should().NotContain("return;");
        setData.Should().Contain("expanderTimer.Stop();");
        setData.Should().Contain("expanderTimer.Start();");
        setData.Should().Contain("SetOnline(true);");
        setData.Should().Contain("OnDeviceInfoChanged();");
    }

    [Fact]
    public void The_Tracker_Status_Follows_The_Expanders_Not_Traffic()
    {
        // A comm monitor on the DSP's connection went OK on any line the DSP sent, and was never
        // started on a serial connection.
        var tracker = Tracker();
        var ctor = Body(tracker, "public TesiraExpanderTracker(");

        tracker.Should().NotContain("GenericCommunicationMonitor");
        ctor.Should().Contain("expander.Monitor.StatusChange +=");
        Body(tracker, "private void UpdateStatus()").Should().Contain("expanderMonitor.SetStatus(");
    }

    [Fact]
    public void The_Tracker_Polls_Whatever_The_Transport()
    {
        var initialize = Body(Tracker(), "protected override void Initialize()");

        initialize.Should().Contain("pollTimer.Start();");
        initialize.Should().Contain("expander.StartMonitoring();");
        initialize.Should().NotContain("isSerialComm");
    }

    [Fact]
    public void Each_Expander_Feedback_Is_Added_Once_Under_A_Key_Of_Its_Own()
    {
        // FeedbackCollection rejects a repeated key. Five fixed names per expander, each added
        // twice, made the tracker - and the DSP that creates it - throw while being created.
        var ctor = Body(Tracker(), "public TesiraExpanderTracker(");

        Count(ctor, "Feedbacks.Add(").Should().Be(5);
        ctor.Should().NotMatchRegex(@"new (String|Bool)Feedback\(""");
        Count(ctor, "FeedbackKey(key, ").Should().Be(5);
    }

    [Fact]
    public void A_Bad_Expander_Entry_Cannot_Stall_The_Queue()
    {
        // The parse runs inside the queue's reply handling; a throw left the queue waiting.
        var parse = Body(Tracker(), "public override void ParseGetMessage(string attributeCode, string message)");

        var loop = parse.IndexOf("for (var v = 0;", StringComparison.Ordinal);
        loop.Should().BeGreaterThan(-1);

        var tryBlock = System.Text.RegularExpressions.Regex.Match(parse.Substring(loop), @"\btry\s*\{");
        tryBlock.Success.Should().BeTrue("the loop body should be inside a try block");

        var guard = loop + tryBlock.Index;
        var setData = parse.IndexOf("newData.SetData(", loop, StringComparison.Ordinal);
        var handler = parse.IndexOf("catch (Exception", loop, StringComparison.Ordinal);

        setData.Should().BeGreaterThan(guard);
        handler.Should().BeGreaterThan(setData);
    }
}
