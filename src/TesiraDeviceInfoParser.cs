using System;
using System.Text.RegularExpressions;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira
{
    /// <summary>
    /// Reads the DEVICE attribute replies the device info component asks for.
    /// </summary>
    /// <remarks>
    /// A Text Protocol reply to a get is <c>+OK "value":&lt;value&gt;</c>: a quoted string for
    /// <c>version</c> and <c>serialNumber</c>, an object of named fields for <c>networkStatus</c>.
    /// Values are read after the <c>"value":</c> key and fields by name, never by position, because
    /// the first quoted string in every reply is the word <c>value</c> itself.
    ///
    /// Free of Crestron and Essentials types so it can be tested on its own.
    /// </remarks>
    public static class TesiraDeviceInfoParser
    {
        private const string ValueKey = "\"value\":";

        /// <summary>
        /// The value of a <c>+OK "value":"..."</c> reply, without its quotes. Null when the reply
        /// carries no value.
        /// </summary>
        public static string Value(string reply)
        {
            if (string.IsNullOrEmpty(reply))
            {
                return null;
            }

            var index = reply.IndexOf(ValueKey, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            var value = reply.Substring(index + ValueKey.Length).Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value.Length == 0 ? null : value;
        }

        /// <summary>
        /// The first string field with this name anywhere in a reply, e.g. <c>"hostname":"TesiraForte"</c>.
        /// Null when the field is absent or empty.
        /// </summary>
        public static string Field(string reply, string name)
        {
            if (string.IsNullOrEmpty(reply) || string.IsNullOrEmpty(name))
            {
                return null;
            }

            var match = Regex.Match(reply, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
        }
    }
}
