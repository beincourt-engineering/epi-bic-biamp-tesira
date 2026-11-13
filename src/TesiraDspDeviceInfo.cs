using System;
using System.Linq;
using System.Text.RegularExpressions;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.DeviceSupport;
using Newtonsoft.Json;
using Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Bridge.JoinMaps.Standalone;
using Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Queue;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.DeviceInfo;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira
{
    public class TesiraDspDeviceInfo : TesiraDspControlPoint, IDeviceInfoProvider
    {
        /// <summary>
        /// Feedback Collection for Component
        /// </summary>

        /// <summary>
        /// A <see cref="TesiraDspDeviceIdentity"/>, so the model travels with the rest of the
        /// device info.
        /// </summary>
        public DeviceInfo DeviceInfo { get { return identity; } }

        private readonly TesiraDspDeviceIdentity identity = new TesiraDspDeviceIdentity();

        public const string Make = "Biamp";

        /// <summary>The model the DSP reports for itself, e.g. TesiraFORTE X 1600.</summary>
        public string Model { get; private set; }

        public event DeviceInfoChangeHandler DeviceInfoChanged;

        private const string keyFormatter = "{0}--{1}";

        private string ipAddress;

        public string IpAddress
        {
            get { return ipAddress; }
            private set
            {
                ipAddress = value;
                IpAddressFeedback.FireUpdate();
            }
        }

        private string macAddress;

        public string MacAddress
        {
            get { return macAddress; }
            private set
            {
                macAddress = value;
                MacAddressFeedback.FireUpdate();
            }
        }

        private string hostname;

        public string Hostname
        {
            get { return hostname; }
            private set
            {
                hostname = value;
                HostnameFeedback.FireUpdate();
            }
        }

        private string serialNumber;

        public string SerialNumber
        {
            get { return serialNumber; }
            private set
            {
                serialNumber = value;
                SerialNumberFeedback.FireUpdate();
            }
        }

        private string firmware;

        public string Firmware
        {
            get { return firmware; }
            private set
            {
                firmware = value;
                FirmwareFeedback.FireUpdate();
            }
        }


        public StringFeedback IpAddressFeedback { get; set; }
        public StringFeedback HostnameFeedback { get; set; }
        public StringFeedback SerialNumberFeedback { get; set; }
        public StringFeedback FirmwareFeedback { get; set; }
        public StringFeedback MacAddressFeedback { get; set; }
        public StringFeedback MakeFeedback { get; set; }
        public StringFeedback ModelFeedback { get; set; }

        /// <summary>
        /// Constructor for Device Info Object
        /// </summary>
        /// <param name="parent">Parent Device</param>
        public TesiraDspDeviceInfo(TesiraDsp parent)
            : base("DEVICE", "DEVICE", 0, 0, parent, string.Format(keyFormatter, parent.Key, "DeviceInfo"), "DeviceInfo", 0)
        {
            Init();
        }

        private void Init()
        {
            NameFeedback = new StringFeedback("name", () => Parent.Name);
            IpAddressFeedback = new StringFeedback("ipAddress", () => IpAddress);
            HostnameFeedback = new StringFeedback("hostname", () => Hostname);
            SerialNumberFeedback = new StringFeedback("serialNumber", () => SerialNumber);
            FirmwareFeedback = new StringFeedback("firmware", () => Firmware);
            MacAddressFeedback = new StringFeedback("macAddress", () => MacAddress);
            MakeFeedback = new StringFeedback("make", () => Make);
            ModelFeedback = new StringFeedback("model", () => Model);

            // The parent's CommandPassthruFeedback is not listed: it changes on every line the DSP
            // sends. LinkToApi links it to the bridge directly.
            Feedbacks.Add(NameFeedback);
            Feedbacks.Add(Parent.CommunicationMonitor.IsOnlineFeedback);
            Feedbacks.Add(IpAddressFeedback);
            Feedbacks.Add(HostnameFeedback);
            Feedbacks.Add(SerialNumberFeedback);
            Feedbacks.Add(FirmwareFeedback);
            Feedbacks.Add(MacAddressFeedback);
        }

        /// <summary>
        /// Seeds hostname/IP from the parent's configured comm object so the bridge reports
        /// something useful before the DSP answers 'DEVICE get networkStatus'. Values are
        /// overwritten by the device's own response when it arrives.
        /// </summary>
        /// <param name="commHostname">Hostname or IP from the comm config; ignored when empty (RS-232)</param>
        internal void SeedFromCommunication(string commHostname)
        {
            if (string.IsNullOrEmpty(commHostname)) return;

            IpAddress = commHostname;
            DeviceInfo.IpAddress = commHostname;

            // An address is not a host name; leave it empty until the DSP names itself.
            if (!System.Net.IPAddress.TryParse(commHostname, out _))
            {
                Hostname = commHostname;
                DeviceInfo.HostName = commHostname;
            }

            OnDeviceInfoChanged();
        }

        /// <summary>Asks again while anything is missing: every minute at first, then every ten.</summary>
        private const long RetryFastMs = 60000;
        private const long RetrySlowMs = 600000;
        private const int FastRetries = 5;

        private readonly object retryLock = new object();
        private CTimer retryTimer;
        private int retries;

        private bool Complete
        {
            get
            {
                // Firmware without deviceInfo never names a model, so the model is only waited
                // for through the fast retries.
                return !string.IsNullOrEmpty(SerialNumber) && !string.IsNullOrEmpty(Firmware)
                    && !string.IsNullOrEmpty(MacAddress)
                    && (!string.IsNullOrEmpty(Model) || retries >= FastRetries);
            }
        }

        public void GetDeviceInfo()
        {
            GetFirmware();
            GetIpConfig();
            GetSerial();
            // After the serial, so its reply can be checked against the serial already read.
            GetModel();

            ScheduleRetry();
        }

        /// <summary>
        /// The questions are asked once per subscribe, and a reply that lands on the wrong command
        /// is ignored, so ask again until serial, firmware and MAC have all arrived.
        /// </summary>
        private void ScheduleRetry()
        {
            lock (retryLock)
            {
                var due = retries < FastRetries ? RetryFastMs : RetrySlowMs;
                if (retryTimer == null)
                {
                    retryTimer = new CTimer(_ => RetryIfIncomplete(), due);
                }
                else
                {
                    retryTimer.Reset(due);
                }
            }
        }

        private void RetryIfIncomplete()
        {
            if (Complete)
            {
                retries = 0;
                return;
            }

            retries++;
            this.LogDebug("Device info incomplete, asking again (attempt {attempt})", retries);
            GetDeviceInfo();
        }


        private void GetIpConfig()
        {
            this.LogVerbose("Getting IPConfig");
            SendFullCommand("get", "networkStatus", null, 999);
        }

        private void GetSerial()
        {
            this.LogVerbose("Getting Serial");

            SendFullCommand("get", "serialNumber", null, 999);
        }

        private void GetFirmware()
        {
            this.LogVerbose("Getting Firmware");

            SendFullCommand("get", "version", null, 999);
        }

        private void GetModel()
        {
            this.LogVerbose("Getting Model");
            SendFullCommand("get", "deviceInfo", null, 999);
        }

        public override void ParseGetMessage(string attributeCode, string message)
        {
            try
            {
                this.LogVerbose("Parsing Message: {message}. AttributeCode: {attributeCode}", message, attributeCode);
                // Parse an "+OK" message

                if (message.IndexOf("+OK", StringComparison.OrdinalIgnoreCase) <= -1) return;

                this.LogDebug("Raw response for {attributeCode}: {message}", attributeCode, message);

                switch (attributeCode)
                {
                    case "networkStatus":
                        {
                            // Null when this isn't a networkStatus reply: replies are matched to
                            // commands by order, and a late one lands on the wrong command.
                            var status = TesiraDeviceInfoParser.NetworkStatus(message);
                            if (status == null)
                            {
                                this.LogDebug("Not a networkStatus reply, ignored: {message}", message);
                                break;
                            }

                            if (status.Hostname != null) Hostname = status.Hostname;
                            MacAddress = status.MacAddress;
                            if (status.IpAddress != null) IpAddress = status.IpAddress;

                            this.LogDebug("Parsed Hostname: {hostname}, MacAddress: {macAddress}, IpAddress: {ipAddress}",
                                Hostname, MacAddress, IpAddress);

                            // Mirror onto the DeviceInfo DTO consumed by IDeviceInfoProvider
                            // clients (Mobile Control, device info reporting).
                            DeviceInfo.HostName = Hostname;
                            DeviceInfo.MacAddress = MacAddress;
                            DeviceInfo.IpAddress = IpAddress;

                            OnDeviceInfoChanged();
                            break;
                        }

                    case "serialNumber":
                        {
                            var serialNumber = TesiraDeviceInfoParser.Serial(message);
                            if (serialNumber == null)
                            {
                                this.LogDebug("Not a serialNumber reply, ignored: {message}", message);
                                break;
                            }

                            SerialNumber = serialNumber;
                            this.LogDebug("Parsed SerialNumber: {serialNumber}", SerialNumber);

                            DeviceInfo.SerialNumber = SerialNumber;

                            OnDeviceInfoChanged();
                            break;
                        }

                    case "version":
                        {
                            var firmware = TesiraDeviceInfoParser.Firmware(message);
                            if (firmware == null)
                            {
                                this.LogDebug("Not a version reply, ignored: {message}", message);
                                break;
                            }

                            Firmware = firmware;
                            this.LogDebug("Parsed Firmware: {firmware}", Firmware);

                            DeviceInfo.FirmwareVersion = Firmware;

                            OnDeviceInfoChanged();
                            break;
                        }

                    case "deviceInfo":
                        {
                            // Null when this isn't a deviceInfo reply, or it doesn't carry the
                            // serial already read: a late reply lands on the wrong command.
                            var model = TesiraDeviceInfoParser.Model(message, SerialNumber);
                            if (model == null)
                            {
                                this.LogDebug("Not a deviceInfo reply, ignored: {message}", message);
                                break;
                            }

                            Model = model;
                            identity.Model = model;
                            this.LogDebug("Parsed Model: {model}", Model);

                            ModelFeedback.FireUpdate();
                            MakeFeedback.FireUpdate();
                            OnDeviceInfoChanged();
                            break;
                        }

                    default:
                        this.LogWarning(
                            "Unhandled device info attributeCode '{attributeCode}'. Response discarded: {message}",
                            attributeCode, message);
                        break;
                }
            }
            catch (Exception e)
            {
                this.LogError("Error parsing device info message: {message}. Error: {error}", message, e);
                this.LogDebug(e, "Stack Trace: ");
            }

        }

        public override void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
        {
            var joinMap = new TesiraDspDeviceJoinMapAdvancedStandalone(joinStart);

            var joinMapSerialized = JoinMapHelper.GetSerializedJoinMapForDevice(joinMapKey);

            if (!string.IsNullOrEmpty(joinMapSerialized))
                joinMap = JsonConvert.DeserializeObject<TesiraDspDeviceJoinMapAdvancedStandalone>(joinMapSerialized);

            bridge?.AddJoinMap(Key, joinMap);

            this.LogDebug("Linking to Trilist {trilistId:X}'", trilist.ID.ToString("X"));

            //var comm = DspDevice as IBasicCommunication;
            trilist.SetSigTrueAction(joinMap.Resubscribe.JoinNumber, Parent.Resubscribe);

            Parent.CommunicationMonitor.IsOnlineFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsOnline.JoinNumber]);
            Parent.CommandPassthruFeedback.LinkInputSig(trilist.StringInput[joinMap.CommandPassThru.JoinNumber]);
            NameFeedback.LinkInputSig(trilist.StringInput[joinMap.Name.JoinNumber]);
            SerialNumberFeedback.LinkInputSig(trilist.StringInput[joinMap.SerialNumber.JoinNumber]);
            FirmwareFeedback.LinkInputSig(trilist.StringInput[joinMap.Firmware.JoinNumber]);
            HostnameFeedback.LinkInputSig(trilist.StringInput[joinMap.Hostname.JoinNumber]);
            IpAddressFeedback.LinkInputSig(trilist.StringInput[joinMap.IpAddress.JoinNumber]);
            MacAddressFeedback.LinkInputSig(trilist.StringInput[joinMap.MacAddress.JoinNumber]);


            trilist.SetStringSigAction(joinMap.CommandPassThru.JoinNumber, (s) => Parent.CommandQueue.EnqueueCommand(s, sendLineRaw: true, priority: (int)CommandPriority.High));

            trilist.OnlineStatusChange += (d, args) =>
            {
                if (!args.DeviceOnLine) return;

                FireAllFeedbacks();
            };

            // The DSP typically answers 'DEVICE get networkStatus' before the bridge activates,
            // so these feedbacks have already fired by the time the sigs are linked above.
            // Push current values now rather than relying on OnlineStatusChange, which only
            // helps when the trilist comes online after linking - not when both start together.
            FireAllFeedbacks();
        }

        internal void FireAllFeedbacks()
        {
            foreach (var feedback in Feedbacks)
            {
                feedback.FireUpdate();
            }
        }

        public void OnDeviceInfoChanged()
        {
            var args = new DeviceInfoEventArgs(DeviceInfo);
            DeviceInfoChanged?.Invoke(Parent, args);
        }


        #region IDeviceInfoProvider Members

        public void UpdateDeviceInfo()
        {
            GetDeviceInfo();
        }

        #endregion

    }
}