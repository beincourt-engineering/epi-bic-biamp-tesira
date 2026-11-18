using System;
using Crestron.SimplSharpPro.DeviceSupport;
using Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Queue;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira
{
    /// <summary>
    /// Watches the DSP's Dante interface and its active faults, for monitoring. Read-only: it only asks
    /// <c>DEVICE get danteInfo</c> (at each new session and every minute) and
    /// <c>DEVICE get activeFaultList</c> (every 30 seconds), at the queue's lowest priority so control
    /// commands always go first.
    /// </summary>
    /// <remarks>
    /// Feedbacks, all on the DSP's own list:
    /// <list type="bullet">
    /// <item><c>dante</c>: JSON. <c>{"present":false}</c> on a DSP without Dante, else the receive and
    /// transmit channel counts, the clock settings and every channel with how many flows it is in, each
    /// marked expected or not in use from the config's <c>dante.expected</c> list. It is asked again
    /// whenever the fault list changes.</item>
    /// <item><c>activeFaults</c>: JSON list of the DSP's active faults, each its id, name and instance.</item>
    /// <item><c>HealthLevel</c> / <c>HealthIssues</c>: ok, warning or fault from the fault list, and the
    /// faults' names joined with "; ".</item>
    /// </list>
    /// Each value is cut to stay under 2000 characters, so a monitoring reader that limits values to
    /// 2048 never receives broken JSON.
    /// </remarks>
    public class TesiraDanteMonitor : TesiraDspControlPoint
    {
        private const string keyFormatter = "{0}--{1}";
        private const double FaultPollMs = 30000;
        // Every minute, not only when the fault list changes: while the DSP's Dante fault stays on (a channel this
        // system doesn't use), a dropped expected flow changes no fault, and only this read shows it. One short reply
        // a minute, at the queue's lowest priority.
        private const double DantePollMs = 60000;

        public StringFeedback DanteFeedback { get; private set; }
        public StringFeedback ActiveFaultsFeedback { get; private set; }
        public StringFeedback HealthLevelFeedback { get; private set; }
        public StringFeedback HealthIssuesFeedback { get; private set; }

        private string dante = "";
        private string activeFaults = "";
        private string healthLevel = "";
        private string healthIssues = "";

        private System.Timers.Timer faultTimer;
        private System.Timers.Timer danteTimer;

        private readonly System.Collections.Generic.List<string> expected;
        private readonly System.Collections.Generic.List<string> devices;

        /// <param name="parent">The DSP.</param>
        /// <param name="expected">The config's <c>dante.expected</c> channel names, or null when not set.</param>
        /// <param name="devices">The config's <c>dante.devices</c> Dante device names, or null when not set.</param>
        public TesiraDanteMonitor(TesiraDsp parent, System.Collections.Generic.IEnumerable<string> expected = null,
            System.Collections.Generic.IEnumerable<string> devices = null)
            : base("DEVICE", "DEVICE", 0, 0, parent, string.Format(keyFormatter, parent.Key, "Dante"), "Dante", 0)
        {
            this.expected = expected == null ? null : new System.Collections.Generic.List<string>(expected);
            this.devices = devices == null ? null : new System.Collections.Generic.List<string>(devices);
            DanteFeedback = new StringFeedback("dante", () => dante);
            ActiveFaultsFeedback = new StringFeedback("activeFaults", () => activeFaults);
            HealthLevelFeedback = new StringFeedback("HealthLevel", () => healthLevel);
            HealthIssuesFeedback = new StringFeedback("HealthIssues", () => healthIssues);

            Feedbacks.Add(DanteFeedback);
            Feedbacks.Add(ActiveFaultsFeedback);
            Feedbacks.Add(HealthLevelFeedback);
            Feedbacks.Add(HealthIssuesFeedback);
        }

        protected override void Initialize()
        {
            faultTimer = new System.Timers.Timer(FaultPollMs) { AutoReset = true };
            faultTimer.Elapsed += (sender, e) =>
            {
                if (Parent.Communication.IsConnected) AskFaults();
            };
            faultTimer.Start();

            danteTimer = new System.Timers.Timer(DantePollMs) { AutoReset = true };
            danteTimer.Elapsed += (sender, e) =>
            {
                if (Parent.Communication.IsConnected) AskDante();
            };
            danteTimer.Start();
        }

        /// <summary>Both questions, as a new session starts.</summary>
        internal void AskAll()
        {
            AskDante();
            AskFaults();
        }

        private void AskDante()
        {
            Enqueue("danteInfo");
        }

        private void AskFaults()
        {
            Enqueue("activeFaultList");
        }

        private void Enqueue(string attribute)
        {
            Parent.CommandQueue.EnqueueCommand(new QueuedCommand(
                string.Format("DEVICE get {0}", attribute), attribute, this, priority: (int)CommandPriority.Low));
        }

        public override void ParseGetMessage(string attributeCode, string message)
        {
            try
            {
                switch (attributeCode)
                {
                    case "danteInfo":
                        if (TesiraDanteParser.IsError(message))
                        {
                            // A DSP doesn't lose its Dante card at run time: once read, an error is a stray reply.
                            if (dante.StartsWith("{\"present\":true", StringComparison.Ordinal))
                            {
                                this.LogDebug("Error reply to danteInfo after it was read, ignored: {message}", message);
                                break;
                            }
                            Set(ref dante, "{\"present\":false}", DanteFeedback);
                            break;
                        }
                        var info = TesiraDanteParser.DanteInfo(message);
                        if (info == null)
                        {
                            this.LogDebug("Not a danteInfo reply, ignored: {message}", message);
                            break;
                        }
                        Set(ref dante, TesiraDanteValues.DanteJson(info, expected, devices), DanteFeedback);
                        // The Dante interface's MACs join the DSP's MAC list after its network interfaces'.
                        var devInfo = Parent.DevInfo;
                        if (devInfo != null)
                        {
                            devInfo.SetDanteMacAddresses(info.PrimaryMac, info.SecondaryMac);
                            // and each Dante port's link and addressing, listed with the DSP's other interfaces
                            devInfo.SetDanteNetwork(TesiraNicParser.DanteNics(message));
                        }
                        break;

                    case "activeFaultList":
                        var faults = TesiraDanteParser.FaultList(message);
                        if (faults == null)
                        {
                            this.LogDebug("Not an activeFaultList reply, ignored: {message}", message);
                            break;
                        }
                        var hadFaults = activeFaults != "";
                        // A changed fault list may mean a flow came or went: ask for the channels' flow counts
                        // now rather than at the next ten-minute poll.
                        if (Set(ref activeFaults, TesiraDanteValues.FaultsJson(faults), ActiveFaultsFeedback) && hadFaults)
                            AskDante();
                        Set(ref healthLevel, faults.Level, HealthLevelFeedback);
                        Set(ref healthIssues, TesiraDanteValues.Issues(faults), HealthIssuesFeedback);
                        break;
                }
            }
            catch (Exception e)
            {
                this.LogError("Error parsing {attribute} reply: {message}. Error: {error}", attributeCode, message, e.Message);
            }
        }

        /// <summary>Fires only when the value changed; true when it did.</summary>
        private static bool Set(ref string field, string value, StringFeedback feedback)
        {
            value = value ?? "";
            if (field == value) return false;
            field = value;
            feedback.FireUpdate();
            return true;
        }

        public override void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
        {
            // Monitoring only: nothing on the bridge.
        }
    }
}
