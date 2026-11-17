using System;
using Crestron.SimplSharpPro.DeviceSupport;
using Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Queue;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core.Bridges;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira
{
    /// <summary>
    /// Reads a VoIP card's own network interface, for monitoring. Read-only: it asks the card's VoIP
    /// control/status block <c>&lt;tag&gt; get network</c> at each new session and every ten minutes, at the
    /// queue's lowest priority, and hands the interface (its MAC, addressing, VLAN and Ethernet setting) to the
    /// device info component, which lists it with the DSP's other interfaces and adds its MAC to the DSP's MACs.
    /// </summary>
    public class TesiraVoipMonitor : TesiraDspControlPoint
    {
        private const string keyFormatter = "{0}--{1}";
        private const double NetworkPollMs = 600000;

        private readonly string tag;
        private readonly string nicName;
        private System.Timers.Timer networkTimer;

        /// <param name="parent">The DSP.</param>
        /// <param name="controlStatusTag">The VoIP control/status block's instance tag.</param>
        /// <param name="nicName">What the interface is called in the DSP's list: voip, or voip and the tag when the DSP has two cards.</param>
        public TesiraVoipMonitor(TesiraDsp parent, string controlStatusTag, string nicName)
            : base(controlStatusTag, controlStatusTag, 0, 0, parent,
                string.Format(keyFormatter, parent.Key, "VoipNetwork-" + controlStatusTag), "VoIP network " + controlStatusTag, 0)
        {
            tag = controlStatusTag;
            this.nicName = nicName;
        }

        protected override void Initialize()
        {
            networkTimer = new System.Timers.Timer(NetworkPollMs) { AutoReset = true };
            networkTimer.Elapsed += (sender, e) =>
            {
                if (Parent.Communication.IsConnected) AskNetwork();
            };
            networkTimer.Start();
        }

        /// <summary>The card's network, as a new session starts.</summary>
        internal void AskNetwork()
        {
            Parent.CommandQueue.EnqueueCommand(new QueuedCommand(
                string.Format("{0} get network", tag), "network", this, priority: (int)CommandPriority.Low));
        }

        public override void ParseGetMessage(string attributeCode, string message)
        {
            try
            {
                if (attributeCode != "network") return;

                var nic = TesiraNicParser.VoipNic(message, nicName);
                if (nic == null)
                {
                    this.LogDebug("Not a VoIP network reply, ignored: {message}", message);
                    return;
                }

                var devInfo = Parent.DevInfo;
                if (devInfo != null) devInfo.SetVoipNic(tag, nic);
            }
            catch (Exception e)
            {
                this.LogError("Error parsing {attribute} reply: {message}. Error: {error}", attributeCode, message, e.Message);
            }
        }

        public override void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
        {
            // Monitoring only: nothing to bridge.
        }
    }
}
