using System;
using System.Collections.Generic;
using System.Linq;
using Crestron.SimplSharpPro.DeviceSupport;
using Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Queue;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira
{
    /// <summary>
    /// The readings a timeline draws for the DSP, for monitoring, on the DSP's own feedback list. A TesiraFORTÉ
    /// reports no CPU, memory, temperature or fan, so these are what changes while it runs: each port's link, its VoIP
    /// lines and calls, and the VoIP card's traffic and uptime. Read-only: the only question it asks is
    /// <c>DEVICE get networkStatus</c> once a minute, at the queue's lowest priority.
    /// </summary>
    /// <remarks>
    /// Feedbacks, each the text of a number or <c>true</c>/<c>false</c>, empty until read:
    /// <list type="bullet">
    /// <item><c>controlLinkUp</c>, <c>controlLinkMbps</c>: the control port, from networkStatus every minute.</item>
    /// <item><c>danteLinkUp</c>, <c>danteSecondaryLinkUp</c>: the Dante ports, from danteInfo (every ten minutes and
    /// whenever the fault list changes).</item>
    /// <item><c>voipLines</c>, <c>voipLinesReady</c>, <c>voipCallsActive</c>: the VoIP lines the config lists, how many
    /// are registered and how many calls are up, counted from the dialers every 15 seconds.</item>
    /// <item><c>voipFramesPerMin</c>, <c>voipCardUptimeS</c>: frames the VoIP cards sent and received a minute, and the
    /// shortest-running card's uptime, from each card's cardStat every minute.</item>
    /// </list>
    /// </remarks>
    public class TesiraTimelineMonitor : TesiraDspControlPoint
    {
        private const string keyFormatter = "{0}--{1}";
        private const double LinkPollMs = 60000;
        private const double LineCountMs = 15000;

        private readonly object valueLock = new object();
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, StringFeedback> feedbacks = new Dictionary<string, StringFeedback>(StringComparer.Ordinal);

        // each VoIP card's last counters, for its frames a minute
        private readonly Dictionary<string, long> lastFrames = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> lastFramesAtMs = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long?> framesPerMin = new Dictionary<string, long?>(StringComparer.Ordinal);
        private readonly Dictionary<string, long?> uptime = new Dictionary<string, long?>(StringComparer.Ordinal);

        private System.Timers.Timer linkTimer;
        private System.Timers.Timer lineTimer;

        public const string ControlLinkUpKey = "controlLinkUp";
        public const string ControlLinkMbpsKey = "controlLinkMbps";
        public const string DanteLinkUpKey = "danteLinkUp";
        public const string DanteSecondaryLinkUpKey = "danteSecondaryLinkUp";
        public const string VoipLinesKey = "voipLines";
        public const string VoipLinesReadyKey = "voipLinesReady";
        public const string VoipCallsActiveKey = "voipCallsActive";
        public const string VoipFramesPerMinKey = "voipFramesPerMin";
        public const string VoipCardUptimeKey = "voipCardUptimeS";

        /// <summary>Every key this monitor publishes, in the order a reader lists them.</summary>
        public static readonly string[] Keys =
        {
            ControlLinkUpKey, ControlLinkMbpsKey, DanteLinkUpKey, DanteSecondaryLinkUpKey,
            VoipLinesKey, VoipLinesReadyKey, VoipCallsActiveKey, VoipFramesPerMinKey, VoipCardUptimeKey,
        };

        public TesiraTimelineMonitor(TesiraDsp parent)
            : base("DEVICE", "DEVICE", 0, 0, parent, string.Format(keyFormatter, parent.Key, "Timeline"), "Timeline", 0)
        {
            foreach (var key in Keys)
            {
                var k = key;
                values[k] = "";
                var feedback = new StringFeedback(k, () => Value(k));
                feedbacks[k] = feedback;
                Feedbacks.Add(feedback);
            }
        }

        private string Value(string key)
        {
            lock (valueLock)
            {
                string v;
                return values.TryGetValue(key, out v) ? v : "";
            }
        }

        private void Set(string key, string value)
        {
            value = value ?? "";
            lock (valueLock)
            {
                if (values[key] == value) return;
                values[key] = value;
            }

            feedbacks[key].FireUpdate();
        }

        protected override void Initialize()
        {
            linkTimer = new System.Timers.Timer(LinkPollMs) { AutoReset = true };
            linkTimer.Elapsed += (sender, e) =>
            {
                if (Parent.Communication.IsConnected) AskLinks();
            };
            linkTimer.Start();

            lineTimer = new System.Timers.Timer(LineCountMs) { AutoReset = true };
            lineTimer.Elapsed += (sender, e) => CountLines();
            lineTimer.Start();
        }

        /// <summary>The control port's link, as a new session starts and every minute.</summary>
        internal void AskLinks()
        {
            Parent.CommandQueue.EnqueueCommand(new QueuedCommand(
                "DEVICE get networkStatus", "networkStatus", this, priority: (int)CommandPriority.Low));
        }

        public override void ParseGetMessage(string attributeCode, string message)
        {
            try
            {
                if (attributeCode != "networkStatus") return;

                var nics = TesiraNicParser.ControlNics(message);
                if (nics.Count == 0)
                {
                    this.LogDebug("Not a networkStatus reply, ignored: {message}", message);
                    return;
                }

                SetControlLink(nics);
                // the device info lists the control port with the DSP's other interfaces: keep its link current there too
                var devInfo = Parent.DevInfo;
                if (devInfo != null) devInfo.SetControlNetwork(message);
            }
            catch (Exception e)
            {
                this.LogError("Error parsing {attribute} reply: {message}. Error: {error}", attributeCode, message, e.Message);
            }
        }

        /// <summary>The control port's link and speed, from a networkStatus reply's interfaces.</summary>
        internal void SetControlLink(List<TesiraNic> nics)
        {
            var control = TesiraTimelineValues.Port(nics, "control") ?? (nics != null ? nics.FirstOrDefault() : null);
            if (control == null) return;
            Set(ControlLinkUpKey, TesiraTimelineValues.Text(control.LinkUp));
            Set(ControlLinkMbpsKey, TesiraTimelineValues.Text(control.LinkMbps));
        }

        /// <summary>The Dante ports' links, from a danteInfo reply's interfaces.</summary>
        internal void SetDanteLinks(List<TesiraNic> nics)
        {
            var primary = TesiraTimelineValues.Port(nics, "dante primary");
            var secondary = TesiraTimelineValues.Port(nics, "dante secondary");
            if (primary != null) Set(DanteLinkUpKey, TesiraTimelineValues.Text(primary.LinkUp));
            if (secondary != null) Set(DanteSecondaryLinkUpKey, TesiraTimelineValues.Text(secondary.LinkUp));
        }

        /// <summary>A VoIP card's counters and uptime, from its control/status block <paramref name="tag"/>.</summary>
        internal void SetVoipCard(string tag, TesiraVoipCardStat stat, long atMs)
        {
            if (stat == null) return;
            long? sum;
            long? shortest;
            lock (valueLock)
            {
                long previous;
                long previousAt;
                var had = lastFrames.TryGetValue(tag, out previous) & lastFramesAtMs.TryGetValue(tag, out previousAt);
                var frames = stat.RxFrames + stat.TxFrames;
                framesPerMin[tag] = TesiraTimelineValues.FramesPerMinute(had ? previous : (long?)null, had ? previousAt : (long?)null, frames, atMs);
                lastFrames[tag] = frames;
                lastFramesAtMs[tag] = atMs;
                uptime[tag] = stat.UptimeS;

                // every card's frames together; a card not yet measured leaves the sum unread
                sum = framesPerMin.Values.Any(v => v == null) ? (long?)null : framesPerMin.Values.Sum(v => v.Value);
                var known = uptime.Values.Where(v => v != null).Select(v => v.Value).ToList();
                shortest = known.Count == 0 ? (long?)null : known.Min();
            }

            Set(VoipFramesPerMinKey, TesiraTimelineValues.Text(sum));
            Set(VoipCardUptimeKey, TesiraTimelineValues.Text(shortest));
        }

        /// <summary>The VoIP lines, how many are registered and how many calls are up, from the dialers.</summary>
        private void CountLines()
        {
            try
            {
                int lines;
                int ready;
                int calls;
                if (!Parent.VoipLineCounts(out lines, out ready, out calls)) return;
                Set(VoipLinesKey, TesiraTimelineValues.Text(lines));
                // not connected, nothing is known about the lines: leave a gap rather than none ready
                if (!Parent.Communication.IsConnected)
                {
                    Set(VoipLinesReadyKey, "");
                    Set(VoipCallsActiveKey, "");
                    return;
                }

                Set(VoipLinesReadyKey, TesiraTimelineValues.Text(ready));
                Set(VoipCallsActiveKey, TesiraTimelineValues.Text(calls));
            }
            catch (Exception e)
            {
                this.LogError("Error counting the VoIP lines: {error}", e.Message);
            }
        }

        public override void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
        {
            // Monitoring only: nothing on the bridge.
        }
    }
}
