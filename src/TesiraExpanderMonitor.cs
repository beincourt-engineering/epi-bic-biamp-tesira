using PepperDash.Core;
using PepperDash.Essentials.Core;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira
{
    /// <summary>
    /// A status monitor whose status is set by its owner from what the DSP reports, rather than
    /// from traffic on a connection. Start and Stop do nothing.
    /// </summary>
    public class TesiraExpanderMonitor : StatusMonitorBase
    {



        public TesiraExpanderMonitor(IKeyed parent, long warningTime, long errorTime)
            : base(parent, warningTime, errorTime)
        {

        }

        public override void Start()
        {
        }

        public override void Stop()
        {
        }

        /// <summary>
        /// Sets the status and the message that explains it. Each change raises StatusChange and
        /// updates IsOnline and IsOnlineFeedback.
        /// </summary>
        /// <param name="status">The new status.</param>
        /// <param name="message">Why, or null when there is nothing to say.</param>
        public void SetStatus(MonitorStatus status, string message)
        {
            Status = status;
            Message = message;
        }

    }
}