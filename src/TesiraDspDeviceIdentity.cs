using System.Collections.Generic;
using PepperDash.Essentials.Core.DeviceInfo;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira
{
    /// <summary>
    /// <see cref="DeviceInfo"/> plus the model, which Essentials' <c>DeviceInfo</c> has no field for.
    /// Consumers that know this type, or read a <c>Model</c> property by name, get the model the DSP
    /// reports for itself.
    /// </summary>
    public class TesiraDspDeviceIdentity : DeviceInfo
    {
        /// <summary>
        /// The model the DSP reports in its <c>DEVICE get deviceInfo</c> reply, e.g.
        /// <c>TesiraFORTE X 1600</c>. Null until the DSP has answered.
        /// </summary>
        public string Model { get; set; }

        /// <summary>
        /// Every MAC the DSP reports, the control interface first, then any other network ports and
        /// the Dante interface's primary and secondary. Null when the DSP reports only its one MAC,
        /// which <see cref="DeviceInfo.MacAddress"/> carries.
        /// </summary>
        public List<string> MacAddresses { get; set; }
    }
}
