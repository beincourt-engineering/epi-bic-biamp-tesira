using System;
using System.Text.RegularExpressions;

// The plugin builds without nullable annotations; the test project links this file and has them on.
#nullable disable

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira
{
    /// <summary>
    /// Reads the DEVICE attribute replies the device info component asks for.
    /// </summary>
    /// <remarks>
    /// The session runs non-verbose, so a reply carries values without their names:
    /// <c>+OK "4.7.2.24086"</c> rather than <c>+OK "value":"4.7.2.24086"</c>, and networkStatus is
    /// its fields in order (hostname, gateway, then each interface's name, MAC and address) rather
    /// than named fields. Both styles are read, because the session starts verbose until the plugin
    /// switches it.
    ///
    /// Replies are matched to commands by order alone, and one late reply shifts every reply after
    /// it onto the wrong command. So each reader also checks the reply has the shape its attribute
    /// gives, and returns null for anything else, rather than store another command's answer.
    ///
    /// Free of Crestron and Essentials types so it can be tested on its own.
    /// </remarks>
    public static class TesiraDeviceInfoParser
    {
        private const string ValueKey = "\"value\":";
        private const string Ok = "+OK";

        private static readonly Regex QuotedString = new Regex("\"([^\"]*)\"");
        private static readonly Regex MacShape = new Regex("^[0-9A-Fa-f]{2}([:-][0-9A-Fa-f]{2}){5}$");
        private static readonly Regex Ipv4Shape = new Regex("^\\d{1,3}(\\.\\d{1,3}){3}$");
        private static readonly Regex VersionShape = new Regex("^\\d+(\\.\\d+){2,3}$");
        private static readonly Regex SerialShape = new Regex("^[A-Za-z0-9]{4,32}$");

        /// <summary>
        /// The value of a reply, verbose or not, without its quotes. Null when the reply carries no
        /// value.
        /// </summary>
        public static string Value(string reply)
        {
            if (string.IsNullOrEmpty(reply))
            {
                return null;
            }

            reply = reply.Trim();
            if (!reply.StartsWith(Ok, StringComparison.Ordinal))
            {
                return null;
            }

            var index = reply.IndexOf(ValueKey, StringComparison.Ordinal);
            var value = index >= 0
                ? reply.Substring(index + ValueKey.Length).Trim()
                : reply.Substring(Ok.Length).Trim();

            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value.Length == 0 ? null : value;
        }

        /// <summary>
        /// The firmware version from a <c>version</c> reply, e.g. <c>4.7.2.24086</c>. Null when the
        /// reply isn't a version.
        /// </summary>
        public static string Firmware(string reply)
        {
            var value = QuotedValue(reply);
            if (value == null || !VersionShape.IsMatch(value))
            {
                return null;
            }

            // An IPv4 address has the same shape. Tesira majors are single digits (4.x, 5.x);
            // control networks start 10, 172 or 192.
            var major = int.Parse(value.Substring(0, value.IndexOf('.')));
            return value.Split('.').Length == 4 && major >= 10 ? null : value;
        }

        /// <summary>
        /// The serial number from a <c>serialNumber</c> reply. Null when the reply isn't one: a
        /// serial is letters and digits only, with at least one digit, so a version, an address or
        /// a host name that arrived in its place is refused.
        /// </summary>
        public static string Serial(string reply)
        {
            var value = QuotedValue(reply);
            if (value == null || !SerialShape.IsMatch(value))
            {
                return null;
            }

            foreach (var c in value)
            {
                if (char.IsDigit(c))
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>
        /// Host name, MAC and IP address from a <c>networkStatus</c> reply. Null when the reply
        /// isn't one, which is when it holds no MAC address.
        /// </summary>
        public static TesiraNetworkStatus NetworkStatus(string reply)
        {
            if (string.IsNullOrEmpty(reply) || !reply.TrimStart().StartsWith(Ok, StringComparison.Ordinal))
            {
                return null;
            }

            if (reply.IndexOf(ValueKey, StringComparison.Ordinal) >= 0)
            {
                var mac = Field(reply, "macAddress");
                if (mac == null || !MacShape.IsMatch(mac))
                {
                    return null;
                }

                return new TesiraNetworkStatus(Field(reply, "hostname"), mac, Field(reply, "ip"));
            }

            // Non-verbose: hostname comes first, then the gateway, then for each interface its
            // name, MAC and address. The control interface is listed first.
            var strings = QuotedString.Matches(reply);
            for (var i = 0; i < strings.Count; i++)
            {
                var value = strings[i].Groups[1].Value;
                if (!MacShape.IsMatch(value))
                {
                    continue;
                }

                string ip = null;
                for (var j = i + 1; j < strings.Count; j++)
                {
                    var next = strings[j].Groups[1].Value;
                    if (MacShape.IsMatch(next))
                    {
                        break;
                    }

                    if (Ipv4Shape.IsMatch(next))
                    {
                        ip = next;
                        break;
                    }
                }

                var hostname = strings[0].Groups[1].Value;
                if (hostname.Length == 0 || MacShape.IsMatch(hostname) || Ipv4Shape.IsMatch(hostname))
                {
                    hostname = null;
                }

                return new TesiraNetworkStatus(hostname, value, ip);
            }

            return null;
        }

        /// <summary>
        /// The first string field with this name anywhere in a verbose reply, e.g.
        /// <c>"hostname":"TesiraForte"</c>. Null when the field is absent or empty.
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

        /// <summary>The value of a reply that is a single quoted string, or null.</summary>
        private static string QuotedValue(string reply)
        {
            if (string.IsNullOrEmpty(reply))
            {
                return null;
            }

            reply = reply.Trim();
            var index = reply.IndexOf(ValueKey, StringComparison.Ordinal);
            var rest = !reply.StartsWith(Ok, StringComparison.Ordinal)
                ? null
                : index >= 0 ? reply.Substring(index + ValueKey.Length).Trim() : reply.Substring(Ok.Length).Trim();

            if (rest == null || rest.Length < 2 || rest[0] != '"' || rest[rest.Length - 1] != '"')
            {
                return null;
            }

            var value = rest.Substring(1, rest.Length - 2);
            return value.Length == 0 || value.IndexOf('"') >= 0 ? null : value;
        }
    }

    /// <summary>What a networkStatus reply says about the control interface.</summary>
    public sealed class TesiraNetworkStatus
    {
        public TesiraNetworkStatus(string hostname, string macAddress, string ipAddress)
        {
            Hostname = hostname;
            MacAddress = macAddress;
            IpAddress = ipAddress;
        }

        public string Hostname { get; private set; }
        public string MacAddress { get; private set; }
        public string IpAddress { get; private set; }
    }
}
