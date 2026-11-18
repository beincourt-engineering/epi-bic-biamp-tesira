using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// The plugin builds without nullable annotations; the test project links this file and has them on.
#nullable disable

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira
{
    /// <summary>A VoIP card's own statistics, from its control/status block's <c>cardStat</c>.</summary>
    public class TesiraVoipCardStat
    {
        /// <summary>Frames the card has received since it started.</summary>
        public long RxFrames { get; set; }

        /// <summary>Frames the card has sent since it started.</summary>
        public long TxFrames { get; set; }

        /// <summary>The card's application firmware (<c>ApploadId</c>), or null.</summary>
        public string Firmware { get; set; }
    }

    /// <summary>
    /// The readings a timeline draws for a DSP, each as the text of a number or <c>true</c>/<c>false</c>, and empty
    /// until it has been read, so a reader leaves a gap rather than drawing a zero. Free of Crestron types, so the
    /// tests exercise it directly.
    /// </summary>
    /// <remarks>
    /// A TesiraFORTÉ reports no CPU, memory, temperature or fan. What it does report that changes while it runs is
    /// each port's link and the VoIP card's traffic.
    /// </remarks>
    public static class TesiraTimelineValues
    {
        /// <summary>
        /// A <c>cardStat</c> reply, verbose
        /// (<c>"value":{"networkStat":{"rxFrames":0 "txFrames":0 "elapsedTime":"028:22:32:15" ...} "firmwareInfo":{...} "callStat":[]}</c>)
        /// or not (the same values by position). Null when the reply isn't one.
        /// </summary>
        public static TesiraVoipCardStat CardStat(string reply)
        {
            var root = TtpValue.Parse(reply);
            if (root == null || !root.IsContainer) return null;

            var net = root.Get("networkStat", 0);
            if (net == null || !net.IsContainer) return null;

            var rx = Count(net.Get("rxFrames", 0));
            var tx = Count(net.Get("txFrames", 1));
            if (rx == null || tx == null) return null;

            var firmware = root.Get("firmwareInfo", 1);
            var app = firmware != null && firmware.IsContainer ? firmware.Get("ApploadId", 1)?.AsText() : null;

            return new TesiraVoipCardStat
            {
                RxFrames = rx.Value,
                TxFrames = tx.Value,
                Firmware = string.IsNullOrWhiteSpace(app) ? null : app.Trim(),
            };
        }

        /// <summary>A frame counter: a whole number, never negative.</summary>
        private static long? Count(TtpNode node)
        {
            var text = node?.AsText();
            long n;
            return text != null && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 ? n : (long?)null;
        }

        /// <summary>
        /// Frames a minute between two reads of the card's counters, rounded. Null for the first read, a read that
        /// came too soon to say, and a counter that went back (the card restarted).
        /// </summary>
        public static long? FramesPerMinute(long? previousFrames, long? previousAtMs, long frames, long atMs)
        {
            if (previousFrames == null || previousAtMs == null) return null;
            var ms = atMs - previousAtMs.Value;
            var delta = frames - previousFrames.Value;
            if (ms < 1000 || delta < 0) return null;
            return (long)Math.Round(delta * 60000.0 / ms, MidpointRounding.AwayFromZero);
        }

        /// <summary>The named port among the DSP's interfaces, or null.</summary>
        public static TesiraNic Port(IEnumerable<TesiraNic> nics, string name)
        {
            return nics?.FirstOrDefault(n => n != null && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A reading's text: empty when it hasn't been read.</summary>
        public static string Text(bool? value)
        {
            return value == null ? "" : value.Value ? "true" : "false";
        }

        /// <inheritdoc cref="Text(bool?)"/>
        public static string Text(long? value)
        {
            return value == null ? "" : value.Value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
