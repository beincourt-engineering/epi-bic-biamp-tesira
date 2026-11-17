using System;
using System.Collections.Generic;
using System.Text;
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
        private static readonly Regex KeyedObject = new Regex("^\"([A-Za-z]+)\"\\s*:\\s*\\{");

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

                return new TesiraNetworkStatus(Field(reply, "hostname"), mac, Field(reply, "ip"), InterfaceMacs(reply));
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

                return new TesiraNetworkStatus(hostname, value, ip, InterfaceMacs(reply));
            }

            return null;
        }

        /// <summary>Most MACs a DSP reports in its list.</summary>
        public const int MaxMacAddresses = 8;

        /// <summary>
        /// The DSP's MAC list: its network interfaces' MACs as networkStatus lists them (control
        /// first), then the Dante interface's primary and secondary. Blanks, anything not shaped like
        /// a MAC and repeats (any case) are left out, at most <see cref="MaxMacAddresses"/>. Null
        /// when that leaves one MAC or none, which the single MAC already says.
        /// </summary>
        public static List<string> MacList(IEnumerable<string> interfaceMacs, string dantePrimary, string danteSecondary)
        {
            var macs = new List<string>();
            var all = new List<string>();
            if (interfaceMacs != null)
            {
                all.AddRange(interfaceMacs);
            }

            all.Add(dantePrimary);
            all.Add(danteSecondary);

            foreach (var raw in all)
            {
                var mac = raw == null ? null : raw.Trim();
                if (string.IsNullOrEmpty(mac) || !MacShape.IsMatch(mac)
                    || macs.Exists(m => string.Equals(m, mac, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                macs.Add(mac);
                if (macs.Count == MaxMacAddresses)
                {
                    break;
                }
            }

            return macs.Count > 1 ? macs : null;
        }

        /// <summary>
        /// Every MAC in a networkStatus reply, in order: one per network interface, the control
        /// interface first. The reply holds no other MAC-shaped value. Repeats left out.
        /// </summary>
        private static List<string> InterfaceMacs(string reply)
        {
            var macs = new List<string>();
            foreach (Match s in QuotedString.Matches(reply))
            {
                var value = s.Groups[1].Value;
                if (MacShape.IsMatch(value) && !macs.Exists(m => string.Equals(m, value, StringComparison.OrdinalIgnoreCase)))
                {
                    macs.Add(value);
                }
            }

            return macs;
        }

        /// <summary>
        /// The model from a <c>deviceInfo</c> reply, e.g. <c>TesiraFORTE X 1600</c>. Null when the
        /// reply isn't one.
        /// </summary>
        /// <remarks>
        /// The attribute returns deviceModel, deviceRevision, serialNumber, firmwareVersion and
        /// ipAddress, in that order. A DSP on firmware 5.7 answers verbose with
        /// <c>+OK "info":{"deviceModel":"TesiraFORTÉ DAN VT" "deviceRevision":"Rev. A" ...}</c> and
        /// non-verbose with a list, <c>+OK ["TesiraFORTÉ DAN VT" "Rev. A" "&lt;serial&gt;" ...]</c>.
        /// Verbose, the model is the <c>deviceModel</c> field (under <c>info</c> or <c>value</c>).
        /// Non-verbose, it is the first value of the list (or of an object body), quoted or not.
        /// The model name can carry an accent (FORTÉ), sent as UTF-8; see <see cref="RepairUtf8"/>.
        ///
        /// Refused: anything not an object, an object holding a MAC address (a networkStatus
        /// reply), and a model that looks like an address or a version. When the serial number is
        /// already known, the reply must also carry it, so a reply that landed on the wrong command
        /// is never taken as the model.
        /// </remarks>
        /// <param name="reply">The line the DSP sent.</param>
        /// <param name="knownSerial">The DSP's serial number when already read, else null.</param>
        public static string Model(string reply, string knownSerial)
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

            var strings = QuotedString.Matches(reply);
            foreach (Match s in strings)
            {
                if (MacShape.IsMatch(s.Groups[1].Value))
                {
                    return null;
                }
            }

            if (!string.IsNullOrEmpty(knownSerial) && reply.IndexOf(knownSerial, StringComparison.Ordinal) < 0)
            {
                return null;
            }

            string model;
            var body = reply.Substring(Ok.Length).Trim();
            var keyed = KeyedObject.Match(body);
            if (keyed.Success)
            {
                // Verbose: "info":{"deviceModel":...} as a DSP sends it, or "value":{...}. Only the
                // named field counts, so another attribute's object is never read as a model.
                if (keyed.Groups[1].Value != "info" && keyed.Groups[1].Value != "value")
                {
                    return null;
                }

                model = Field(body, "deviceModel") ?? BareField(body, "deviceModel");
            }
            else if (body.StartsWith("[", StringComparison.Ordinal) || body.StartsWith("{", StringComparison.Ordinal))
            {
                // Non-verbose: the values in order, as a list ["model" "rev" "serial" ...] as a DSP
                // sends it, or an object body. The model is the first.
                model = FirstValue(body.Substring(1));
            }
            else
            {
                return null;
            }

            if (model == null)
            {
                return null;
            }

            model = RepairUtf8(model).Trim();
            if (model.Length == 0 || model.Length > 64 || Ipv4Shape.IsMatch(model) || VersionShape.IsMatch(model))
            {
                return null;
            }

            foreach (var c in model)
            {
                if (char.IsLetter(c))
                {
                    return model;
                }
            }

            return null;
        }

        /// <summary>
        /// Text the DSP sent as UTF-8 but that arrived read one byte per character (a telnet
        /// session decodes Latin-1), put back: <c>FORTÃ‰</c> becomes <c>FORTÉ</c>. Anything that
        /// isn't such text comes back as it was.
        /// </summary>
        public static string RepairUtf8(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var high = false;
            foreach (var c in text)
            {
                if (c > 'ÿ')
                {
                    return text;
                }

                if (c >= '\u0080')
                {
                    high = true;
                }
            }

            if (!high)
            {
                return text;
            }

            try
            {
                var bytes = Encoding.GetEncoding(28591).GetBytes(text);
                return new UTF8Encoding(false, true).GetString(bytes, 0, bytes.Length);
            }
            catch (ArgumentException)
            {
                // Not UTF-8 once read as bytes, so it was Latin-1 all along.
                return text;
            }
        }

        /// <summary>The first value in an object's body: a quoted string, or a bare word.</summary>
        private static string FirstValue(string body)
        {
            body = body.TrimStart();
            if (body.Length == 0)
            {
                return null;
            }

            if (body[0] == '"')
            {
                var end = body.IndexOf('"', 1);
                return end < 0 ? null : body.Substring(1, end - 1);
            }

            var word = Regex.Match(body, "^[A-Za-z0-9_.\\-]+");
            return word.Success ? word.Value : null;
        }

        /// <summary>An unquoted field, e.g. <c>"deviceModel":TESIRAFORTE_X_1600</c>.</summary>
        private static string BareField(string reply, string name)
        {
            var match = Regex.Match(reply, "\"" + Regex.Escape(name) + "\"\\s*:\\s*([A-Za-z0-9_.\\-]+)");
            return match.Success ? match.Groups[1].Value : null;
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
            : this(hostname, macAddress, ipAddress, null)
        {
        }

        public TesiraNetworkStatus(string hostname, string macAddress, string ipAddress, List<string> macAddresses)
        {
            Hostname = hostname;
            MacAddress = macAddress;
            IpAddress = ipAddress;
            MacAddresses = macAddresses ?? (macAddress == null ? new List<string>() : new List<string> { macAddress });
        }

        public string Hostname { get; private set; }

        /// <summary>The control interface's MAC, the first one listed.</summary>
        public string MacAddress { get; private set; }

        public string IpAddress { get; private set; }

        /// <summary>
        /// Every interface's MAC in the order the DSP lists them, the control interface first. A
        /// TesiraFORTÉ lists one; a TesiraSERVER lists one per network port.
        /// </summary>
        public List<string> MacAddresses { get; private set; }
    }
}
