using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Crestron.SimplSharpPro.DeviceSupport;
using Newtonsoft.Json;
using Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Bridge.JoinMaps;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Expander
{
    public class TesiraExpanderTracker : TesiraDspControlPoint, ICommunicationMonitor
    {

        private const string keyFormatter = "{0}--{1}";
        public List<TesiraExpanderData> Expanders = new List<TesiraExpanderData>();

        public Dictionary<int, StringFeedback> Hostnames = new Dictionary<int, StringFeedback>();
        public Dictionary<int, StringFeedback> SerialNumbers = new Dictionary<int, StringFeedback>();
        public Dictionary<int, StringFeedback> Firmwares = new Dictionary<int, StringFeedback>();
        public Dictionary<int, StringFeedback> MacAddresses = new Dictionary<int, StringFeedback>();
        public Dictionary<int, BoolFeedback> OnlineStatuses = new Dictionary<int, BoolFeedback>();

        /// <summary>
        /// The expanders taken together: OK while the DSP reports every configured expander, a
        /// warning while it is missing some and an error while it is missing all, with the missing
        /// ones named in the message. Unknown until each has reported or timed out.
        /// </summary>
        public StatusMonitorBase CommunicationMonitor { get; private set; }

        private readonly TesiraExpanderMonitor expanderMonitor;
        private readonly object statusLock = new object();

        /// <summary>
        /// How often the DSP is asked which expanders it sees. Each answer restarts the reported
        /// expanders' countdowns to offline, so this must stay well inside them.
        /// </summary>
        private const double PollIntervalMs = 45000;

        private System.Timers.Timer pollTimer;


        public TesiraExpanderTracker(TesiraDsp parent, Dictionary<string, TesiraExpanderBlockConfig> expanders)
            : base("", "", 0, 0, parent, string.Format(keyFormatter, parent.Key, "Expanders"), "Expanders", 0)
        {
            expanderMonitor = new TesiraExpanderMonitor(this, 90000, 180000);
            CommunicationMonitor = expanderMonitor;

            foreach (var tesiraExpanderBlockConfig in expanders)
            {
                var key = tesiraExpanderBlockConfig.Value.Index;
                var value = tesiraExpanderBlockConfig.Value.Hostname;
                var expander = new TesiraExpanderData(value, key, this, CheckTracker);
                Expanders.Add(expander);

                // Feedback keys carry the expander's index: FeedbackCollection rejects a repeated
                // key, and every expander's feedbacks are on this one list.
                var h = new StringFeedback(FeedbackKey(key, "hostName"), () => expander.Hostname);
                var s = new StringFeedback(FeedbackKey(key, "serialNumber"), () => expander.SerialNumber);
                var f = new StringFeedback(FeedbackKey(key, "firmware"), () => expander.Firmware);
                var m = new StringFeedback(FeedbackKey(key, "macAddress"), () => expander.MacAddress);
                var o = new BoolFeedback(FeedbackKey(key, "online"), () => expander.Online);

                Hostnames.Add(key, h);
                SerialNumbers.Add(key, s);
                Firmwares.Add(key, f);
                MacAddresses.Add(key, m);
                OnlineStatuses.Add(key, o);

                Feedbacks.Add(h);
                Feedbacks.Add(s);
                Feedbacks.Add(f);
                Feedbacks.Add(m);
                Feedbacks.Add(o);

                expander.Monitor.StatusChange += (sender, args) =>
                {
                    o.FireUpdate();
                    UpdateStatus();
                };

                DeviceManager.AddDevice(expander);

            }
            this.LogVerbose("There are {count} configured expanders", Expanders.Count);

            foreach (var item in Expanders)
            {
                this.LogVerbose("Expander Index = {index} ; Expander Hostname = {hostname}", item.Index, item.Hostname);
            }

            UpdateStatus();
        }

        private string FeedbackKey(int index, string name)
        {
            return string.Format("{0}-{1}-{2}", Key, index, name);
        }

        protected override void Initialize()
        {
            CheckTracker();

            // Ask on a timer whatever the transport, and count each expander from now, so one the
            // DSP never reports goes offline instead of staying unknown.
            foreach (var expander in Expanders)
            {
                expander.StartMonitoring();
            }

            pollTimer = new System.Timers.Timer(PollIntervalMs) { AutoReset = true };
            pollTimer.Elapsed += (sender, e) =>
            {
                if (Parent.Communication.IsConnected)
                    CheckTracker();
            };
            pollTimer.Start();
        }

        /// <summary>
        /// Sets this tracker's status from the expanders' own.
        /// </summary>
        private void UpdateStatus()
        {
            lock (statusLock)
            {
                var missing = Expanders
                    .Where(e => e.Monitor.Status == MonitorStatus.InError)
                    .Select(e => e.Name)
                    .ToList();

                if (missing.Count == 0)
                {
                    var waiting = Expanders.Any(e => e.Monitor.Status == MonitorStatus.StatusUnknown);
                    expanderMonitor.SetStatus(waiting ? MonitorStatus.StatusUnknown : MonitorStatus.IsOk, null);
                    return;
                }

                expanderMonitor.SetStatus(
                    missing.Count == Expanders.Count ? MonitorStatus.InError : MonitorStatus.InWarning,
                    "Expanders not reported by the DSP: " + string.Join(", ", missing));
            }
        }


        internal void CheckTracker()
        {
            this.LogVerbose("Getting DiscoveredExpanders");

            SendFullCommand("get", "discoveredExpanders", null, 999);
        }

        const string pattern = @"\[([^\[\]]+)\]?";
        private readonly static Regex regex1 = new Regex(pattern);
        const string pattern2 = "\\\"([^\\\"\\\"]+)\\\"?";
        private readonly static Regex regex2 = new Regex(pattern2);



        public override void ParseGetMessage(string attributeCode, string message)
        {
            this.LogVerbose("!!!!!!!!EXPANDER DATA!!!!!!!!!!!");

            var matches = regex1.Matches(message);

            this.LogVerbose("There are {matchCount} Matches", matches.Count);
            for (var v = 0; v < matches.Count; v++)
            {
                // This runs while the command queue waits on this reply: an entry that does not
                // parse is logged and skipped, so it cannot hold up the queue or the other entries.
                try
                {
                    if (!matches[v].ToString().Contains('"')) continue;
                    this.LogVerbose("Match {matchIndex} is a device", v);

                    var matchesEnclosed = regex2.Matches(matches[v].ToString());
                    var data2 = regex2.Replace(matches[v].ToString(), "").Trim('"').Trim('[').Trim().Replace("  ", " ");
                    Console.WriteLine("Data2 = {0}", data2);
                    var hostname = matchesEnclosed[0].ToString().Trim('"');

                    this.LogVerbose("Match {matchIndex} Hostname : {hostname}", v, hostname);

                    var newData = Expanders.FirstOrDefault(o => String.Equals(o.Hostname, hostname, StringComparison.CurrentCultureIgnoreCase));

                    if (newData == null) continue;
                    this.LogVerbose("Found a device Index {deviceIndex} with Hostname {hostname}", newData.Index, newData.Hostname);
                    var macData = matches[v + 1].ToString();
                    var otherData = matches[v].ToString();
                    newData.SetData(otherData, macData);
                }
                catch (Exception ex)
                {
                    this.LogError("Could not parse expander entry {matchIndex} in '{message}': {error}", v, message, ex.Message);
                    this.LogVerbose(ex, "Stack trace:");
                }
            }

            foreach (var feedback in Feedbacks)
            {
                var f = feedback;
                f.FireUpdate();
            }

            foreach (var device in Expanders)
            {
                var i = device;

                this.LogVerbose("Index = {index}", i.Index);
                this.LogVerbose("Hostname = {hostname}", i.Hostname);
                this.LogVerbose("MacAddress = {macAddress}", i.MacAddress);
                this.LogVerbose("SerialNumber = {serialNumber}", i.SerialNumber);
                this.LogVerbose("Firmware = {firmware}", i.Firmware);
                this.LogVerbose("Online = {online}", i.Online);
            }
        }

        public override void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
        {
            var expanderJoinMap = new TesiraExpanderJoinMap(joinStart);


            var joinMapSerialized = JoinMapHelper.GetSerializedJoinMapForDevice(joinMapKey);

            if (!string.IsNullOrEmpty(joinMapSerialized))
                expanderJoinMap = JsonConvert.DeserializeObject<TesiraExpanderJoinMap>(joinMapSerialized);


            var expanderJoinMapSerialized = JoinMapHelper.GetSerializedJoinMapForDevice(joinMapKey);

            if (!string.IsNullOrEmpty(expanderJoinMapSerialized))
            {
                expanderJoinMap =
                    JsonConvert.DeserializeObject<TesiraExpanderJoinMap>(expanderJoinMapSerialized);
            }

            bridge?.AddJoinMap(Key, expanderJoinMap);

            this.LogDebug("Linking to Trilist {trilist:X}", trilist.ID);

            foreach (var item in Expanders)
            {
                if (item == null) return;

                var expander = item;
                var index = expander.Index;
                var offset = 5 * (index - 1);


                Hostnames[index].LinkInputSig(trilist.StringInput[(uint)(expanderJoinMap.Hostname.JoinNumber + offset)]);
                SerialNumbers[index].LinkInputSig(trilist.StringInput[(uint)(expanderJoinMap.SerialNumber.JoinNumber + offset)]);
                Firmwares[index].LinkInputSig(trilist.StringInput[(uint)(expanderJoinMap.Firmware.JoinNumber + offset)]);
                MacAddresses[index].LinkInputSig(trilist.StringInput[(uint)(expanderJoinMap.MacAddress.JoinNumber + offset)]);
                OnlineStatuses[index].LinkInputSig(trilist.BooleanInput[(uint)(expanderJoinMap.IsOnline.JoinNumber + offset)]);
            }

            trilist.OnlineStatusChange += (d, args) =>
            {
                if (!args.DeviceOnLine) return;

                foreach (var feedback in Feedbacks)
                {
                    feedback.FireUpdate();
                }

            };
        }
    }
}


