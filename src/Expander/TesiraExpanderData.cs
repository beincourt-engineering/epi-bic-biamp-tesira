using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Timers;
using Crestron.SimplSharp;
using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.DeviceInfo;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Expander
{
  public class TesiraExpanderData : IDeviceInfoProvider, IKeyName, IOnline, ICommunicationMonitor
  {
    /// <summary>
    /// How long the DSP may go without reporting this expander before it counts as offline. The
    /// tracker asks every 45 seconds, so one missed answer is not enough.
    /// </summary>
    private const double OfflineAfterMs = 120000;

    // One-shot: each report restarts it, and if it runs out the expander is offline.
    private readonly System.Timers.Timer expanderTimer;
    private readonly object stateLock = new object();

    public DeviceInfo DeviceInfo { get; private set; }
    public event DeviceInfoChangeHandler DeviceInfoChanged;
    private readonly Action dataPoll;

    public StringFeedback NameFeedback { get; private set; }

    public BoolFeedback IsOnline { get; private set; }
    public bool Online { get; private set; }
    public string Hostname { get; private set; }
    public string SerialNumber { get; private set; }
    public string Firmware { get; private set; }
    public string MacAddress { get; private set; }
    public int Index { get; private set; }
    public string Key { get; private set; }
    public string Name { get; private set; }
    public TesiraExpanderMonitor Monitor;

    /// <summary>
    /// Unknown until monitoring starts and the DSP first reports this expander; OK while it keeps
    /// reporting it; in error once it has not for <see cref="OfflineAfterMs"/>.
    /// </summary>
    public StatusMonitorBase CommunicationMonitor
    {
      get { return Monitor; }
    }

    private const string expanderPattern = "\\\"([^\\\"\\\"]+)\\\"?";
    private static readonly Regex expanderRegex = new Regex(expanderPattern);

    public TesiraExpanderData(string data, int index, IKeyed parent, Action dataPoll)
    {
      Key = String.Format("{0}-{1}", parent.Key, data);
      Name = data;
      Online = false;
      Index = index;
      Hostname = data;
      SerialNumber = "";
      Firmware = "";
      MacAddress = "";
      this.dataPoll = dataPoll;

      IsOnline = new BoolFeedback("online", () => Online);
      NameFeedback = new StringFeedback("name", () => Name);

      Monitor = new TesiraExpanderMonitor(this, 180000, 360000);
      DeviceInfo = new DeviceInfo();
      Monitor.Start();

      expanderTimer = new System.Timers.Timer(OfflineAfterMs) { AutoReset = false };
      expanderTimer.Elapsed += (sender, e) => SetOffline();
    }

    /// <summary>
    /// Starts the countdown to offline if no report has started it, so an expander the DSP never
    /// reports ends up offline rather than unknown. Called once the tracker starts asking.
    /// </summary>
    internal void StartMonitoring()
    {
      lock (stateLock)
      {
        if (!expanderTimer.Enabled)
          expanderTimer.Start();
      }
    }

    private void SetOffline()
    {
      lock (stateLock)
      {
        // A report restarted the countdown as it ran out: that report wins.
        if (expanderTimer.Enabled) return;

        SetOnline(false);
      }
    }

    private void SetOnline(bool online)
    {
      Online = online;
      IsOnline.FireUpdate();
      Monitor.SetStatus(online ? MonitorStatus.IsOk : MonitorStatus.InError,
        online ? null : string.Format("Not reported by the DSP for {0} seconds", OfflineAfterMs / 1000));
    }

    public void SetData(string data, string macData)
    {
      var matches = expanderRegex.Matches(data);
      var data2 = expanderRegex.Replace(data, "").Trim('"').Trim('[').Trim().Replace("  ", " ");

      var fData = data2.Split(' ');
      Hostname = matches[0].ToString().Trim('"');
      SerialNumber = matches[1].ToString().Trim('"');

      Firmware = string.Format("{0}.{1}.{2}-build{3}", fData[0], fData[1], fData[2], fData[3]);

      MacAddress = FormatMac(macData);

      DeviceInfo.HostName = Hostname;
      DeviceInfo.SerialNumber = SerialNumber;
      DeviceInfo.FirmwareVersion = Firmware;
      DeviceInfo.MacAddress = MacAddress;

      lock (stateLock)
      {
        expanderTimer.Stop();
        expanderTimer.Start();

        SetOnline(true);
      }

      OnDeviceInfoChanged();
    }

    private static string FormatMac(string data)
    {
      var newData = data.Replace("[", "").Replace("]", "");
      var macGroup = newData.Split(' ');
      var mac = macGroup.Aggregate("", (current, oct) => current + int.Parse(oct).ToString("X2") + ":").Trim(':');
      return mac;
    }


    private void OnDeviceInfoChanged()
    {
      var handler = DeviceInfoChanged;
      if (handler == null) return;
      handler(this, new DeviceInfoEventArgs(DeviceInfo));
    }


    #region IDeviceInfoProvider Members


    public void UpdateDeviceInfo()
    {
      if (dataPoll == null) return;
      dataPoll.Invoke();
    }

    #endregion
  }
}


