using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

// The plugin builds without nullable annotations; the test project links this file and has them on.
#nullable disable

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira
{
    /// <summary>
    /// One value in a TTP reply: an object <c>{...}</c>, an array <c>[...]</c>, a quoted string or a bare
    /// word (numbers, true/false, enum values such as RX). In a verbose session each item of an object
    /// carries its name (<c>"name":value</c>); in a non-verbose one the items are only in order.
    /// </summary>
    public sealed class TtpNode
    {
        public enum NodeKind { Object, Array, String, Bare }

        public NodeKind Kind { get; internal set; }

        /// <summary>The item's name in a verbose reply, else null.</summary>
        public string Name { get; internal set; }

        /// <summary>A string's or bare word's text.</summary>
        public string Text { get; internal set; }

        public List<TtpNode> Items { get; } = new List<TtpNode>();

        public bool IsContainer
        {
            get { return Kind == NodeKind.Object || Kind == NodeKind.Array; }
        }

        /// <summary>The item with this name, or null (always null in a non-verbose reply).</summary>
        public TtpNode Field(string name)
        {
            return Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
        }

        /// <summary>The item at this place, or null.</summary>
        public TtpNode At(int index)
        {
            return index >= 0 && index < Items.Count ? Items[index] : null;
        }

        /// <summary>By name when the reply is verbose, else by place.</summary>
        public TtpNode Get(string name, int index)
        {
            return Items.Any(i => i.Name != null) ? Field(name) : At(index);
        }

        public bool? AsBool()
        {
            if (Kind != NodeKind.Bare && Kind != NodeKind.String) return null;
            if (string.Equals(Text, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(Text, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }

        public int? AsInt()
        {
            int n;
            return (Kind == NodeKind.Bare || Kind == NodeKind.String)
                && int.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : (int?)null;
        }

        public string AsText()
        {
            return Kind == NodeKind.String || Kind == NodeKind.Bare ? Text : null;
        }
    }

    /// <summary>Reads a TTP reply's value into <see cref="TtpNode"/>s. Free of Crestron types.</summary>
    public static class TtpValue
    {
        private const string Ok = "+OK";

        /// <summary>
        /// The value of a <c>+OK</c> reply, whether it is introduced by <c>"value":</c>, by the attribute's
        /// own name (<c>"danteInfo":</c>) or by nothing. Null for an error, an empty reply or one that
        /// doesn't parse.
        /// </summary>
        public static TtpNode Parse(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return null;
            reply = reply.Trim();
            if (!reply.StartsWith(Ok, StringComparison.Ordinal)) return null;

            var pos = Ok.Length;
            try
            {
                var node = Read(reply, ref pos);
                SkipSpace(reply, ref pos);
                return pos == reply.Length ? node : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static void SkipSpace(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }

        /// <summary>One value, with the name in front of it when there is one.</summary>
        private static TtpNode Read(string s, ref int pos)
        {
            SkipSpace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("value expected");

            string name = null;
            if (s[pos] == '"')
            {
                var save = pos;
                var text = ReadString(s, ref pos);
                if (pos < s.Length && s[pos] == ':')
                {
                    pos++;
                    name = text;
                }
                else
                {
                    pos = save;
                }
            }

            SkipSpace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("value expected");

            TtpNode node;
            var c = s[pos];
            if (c == '{' || c == '[')
            {
                var close = c == '{' ? '}' : ']';
                node = new TtpNode { Kind = c == '{' ? TtpNode.NodeKind.Object : TtpNode.NodeKind.Array };
                pos++;
                while (true)
                {
                    SkipSpace(s, ref pos);
                    if (pos >= s.Length) throw new FormatException("unclosed " + c);
                    if (s[pos] == close)
                    {
                        pos++;
                        break;
                    }
                    if (s[pos] == ',')
                    {
                        pos++;
                        continue;
                    }
                    node.Items.Add(Read(s, ref pos));
                }
            }
            else if (c == '"')
            {
                node = new TtpNode { Kind = TtpNode.NodeKind.String, Text = ReadString(s, ref pos) };
            }
            else if (c == '}' || c == ']' || c == ':')
            {
                throw new FormatException("unexpected " + c);
            }
            else
            {
                var start = pos;
                while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && "{}[]\",".IndexOf(s[pos]) < 0) pos++;
                node = new TtpNode { Kind = TtpNode.NodeKind.Bare, Text = s.Substring(start, pos - start) };
            }

            node.Name = name;
            return node;
        }

        private static string ReadString(string s, ref int pos)
        {
            var sb = new StringBuilder();
            pos++; // opening quote
            while (pos < s.Length)
            {
                var c = s[pos++];
                if (c == '\\' && pos < s.Length)
                {
                    sb.Append(s[pos++]);
                    continue;
                }
                if (c == '"') return sb.ToString();
                sb.Append(c);
            }
            throw new FormatException("unclosed string");
        }
    }

    /// <summary>A Dante channel as <c>DEVICE get danteInfo</c> lists it.</summary>
    public sealed class TesiraDanteChannel
    {
        public int Number { get; set; }
        public string Name { get; set; }
        public bool Receive { get; set; }

        /// <summary>
        /// Whether the DSP raises a fault when this channel goes inactive. Set per receive channel in the
        /// design, for the channels that should always carry audio.
        /// </summary>
        public bool FaultOnInactive { get; set; }
    }

    /// <summary>What <c>DEVICE get danteInfo</c> says about the DSP's Dante interface.</summary>
    public sealed class TesiraDanteInfo
    {
        public bool? PreferredLeader { get; set; }
        public bool? FollowerOnly { get; set; }
        public int? LatencyMicroseconds { get; set; }
        public List<TesiraDanteChannel> Channels { get; } = new List<TesiraDanteChannel>();

        public IEnumerable<TesiraDanteChannel> Receive
        {
            get { return Channels.Where(c => c.Receive); }
        }

        public IEnumerable<TesiraDanteChannel> Transmit
        {
            get { return Channels.Where(c => !c.Receive); }
        }
    }

    /// <summary>One fault the DSP reports in <c>DEVICE get activeFaultList</c>.</summary>
    public sealed class TesiraFault
    {
        public string Id { get; set; }
        public string Name { get; set; }

        /// <summary>The indicator of the device entry the fault was listed under.</summary>
        public string Indicator { get; set; }
    }

    /// <summary>The DSP's active faults, and the level they add up to.</summary>
    public sealed class TesiraFaultList
    {
        public List<TesiraFault> Faults { get; } = new List<TesiraFault>();

        /// <summary><c>ok</c>, <c>warning</c> or <c>fault</c>, from the worst indicator listed.</summary>
        public string Level { get; set; }
    }

    /// <summary>
    /// Reads the two DEVICE attributes the Dante monitor asks for. Like <see cref="TesiraDeviceInfoParser"/>,
    /// each reader checks the reply has its attribute's shape and returns null for anything else, because
    /// replies are matched to commands by order alone and a late one lands on the wrong command.
    /// </summary>
    public static class TesiraDanteParser
    {
        /// <summary>
        /// A <c>danteInfo</c> reply. The attribute holds, in order: revs, netCfg, allNetStatus,
        /// preferredDanteNetworkClockSource, networkClockSlaveOnly, networkLatencyInMicroseconds and chInfos,
        /// each channel being name, number, txrx and faultOnInactive. Null when the reply isn't one.
        /// </summary>
        public static TesiraDanteInfo DanteInfo(string reply)
        {
            var root = TtpValue.Parse(reply);
            if (root == null || root.Kind != TtpNode.NodeKind.Object) return null;

            var channels = root.Get("chInfos", 6);
            if (channels == null || channels.Kind != TtpNode.NodeKind.Array) return null;

            var info = new TesiraDanteInfo
            {
                PreferredLeader = root.Get("preferredDanteNetworkClockSource", 3)?.AsBool(),
                FollowerOnly = root.Get("networkClockSlaveOnly", 4)?.AsBool(),
                LatencyMicroseconds = root.Get("networkLatencyInMicroseconds", 5)?.AsInt(),
            };

            foreach (var ch in channels.Items)
            {
                if (ch.Kind != TtpNode.NodeKind.Object) return null;
                var name = ch.Get("name", 0)?.AsText();
                var number = ch.Get("number", 1)?.AsInt();
                var txrx = ch.Get("txrx", 2)?.AsText();
                var fault = ch.Get("faultOnInactive", 3)?.AsBool();
                var receive = string.Equals(txrx, "RX", StringComparison.OrdinalIgnoreCase);
                if (name == null || number == null || (!receive && !string.Equals(txrx, "TX", StringComparison.OrdinalIgnoreCase)))
                    return null;

                info.Channels.Add(new TesiraDanteChannel { Number = number.Value, Name = name, Receive = receive, FaultOnInactive = fault == true });
            }

            return info;
        }

        /// <summary>True for an error reply, as a DSP without a Dante interface answers <c>danteInfo</c>.</summary>
        public static bool IsError(string reply)
        {
            return reply != null && reply.TrimStart().StartsWith("-ERR", StringComparison.Ordinal);
        }

        /// <summary>
        /// An <c>activeFaultList</c> reply: one entry per device in the system, each an indicator
        /// (<c>INDICATOR_...</c>), its name, the faults under it (each an id and a name) and the device's
        /// serial number. Null when the reply isn't one.
        /// </summary>
        public static TesiraFaultList FaultList(string reply)
        {
            var root = TtpValue.Parse(reply);
            if (root == null || root.Kind != TtpNode.NodeKind.Array) return null;

            var list = new TesiraFaultList { Level = "ok" };
            foreach (var entry in root.Items)
            {
                if (entry.Kind != TtpNode.NodeKind.Object) return null;
                var indicator = entry.Get("id", 0)?.AsText();
                if (indicator == null || !indicator.StartsWith("INDICATOR_", StringComparison.Ordinal)) return null;

                var level = LevelOf(indicator);
                if (Worse(level, list.Level)) list.Level = level;

                var faults = entry.Get("faults", 2);
                var listed = 0;
                if (faults != null && faults.Kind == TtpNode.NodeKind.Array)
                {
                    foreach (var f in faults.Items)
                    {
                        if (f.Kind != TtpNode.NodeKind.Object) continue;
                        var id = f.Get("id", 0)?.AsText();
                        var name = f.Get("name", 1)?.AsText();
                        if (id == null && name == null) continue;
                        list.Faults.Add(new TesiraFault { Id = id, Name = name, Indicator = indicator });
                        listed++;
                    }
                }

                // An entry that is in fault but lists nothing under it still says what is wrong in its name.
                if (listed == 0 && level != "ok")
                    list.Faults.Add(new TesiraFault { Id = indicator, Name = entry.Get("name", 1)?.AsText(), Indicator = indicator });
            }

            return list;
        }

        /// <summary><c>INDICATOR_NONE_...</c> is ok, <c>..._MINOR_...</c> a warning, anything else a fault.</summary>
        public static string LevelOf(string indicator)
        {
            if (indicator == null) return "unknown";
            if (indicator.IndexOf("_NONE", StringComparison.Ordinal) >= 0) return "ok";
            if (indicator.IndexOf("_MINOR", StringComparison.Ordinal) >= 0) return "warning";
            return "fault";
        }

        private static readonly string[] Order = { "ok", "unknown", "warning", "fault" };

        private static bool Worse(string a, string b)
        {
            return Array.IndexOf(Order, a) > Array.IndexOf(Order, b);
        }

        /// <summary>A fault that concerns Dante, by its id or name.</summary>
        public static bool IsDante(TesiraFault fault)
        {
            return fault != null
                && ((fault.Id ?? "").IndexOf("DANTE", StringComparison.OrdinalIgnoreCase) >= 0
                    || (fault.Name ?? "").IndexOf("Dante", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    /// <summary>
    /// The monitor's feedback values, built from the parsed replies. Each is kept under
    /// <see cref="MaxValueChars"/>, so a monitoring reader that limits values to 2048 characters never
    /// receives broken JSON.
    /// </summary>
    public static class TesiraDanteValues
    {
        public const int MaxValueChars = 2000;

        /// <summary>The Dante interface as the <c>dante</c> feedback carries it.</summary>
        public static string DanteJson(TesiraDanteInfo info)
        {
            var watched = info.Receive.Where(c => c.FaultOnInactive).Select(c => new object[] { c.Number, c.Name }).ToList();
            var value = new Dictionary<string, object>
            {
                { "present", true },
                { "rx", info.Receive.Count() },
                { "tx", info.Transmit.Count() },
                { "preferredLeader", info.PreferredLeader },
                { "followerOnly", info.FollowerOnly },
                { "latencyUs", info.LatencyMicroseconds },
                { "watched", watched },
            };

            var json = JsonConvert.SerializeObject(value);
            var dropped = 0;
            while (json.Length > MaxValueChars && watched.Count > 0)
            {
                watched.RemoveAt(watched.Count - 1);
                dropped++;
                value["more"] = dropped;
                json = JsonConvert.SerializeObject(value);
            }
            return json;
        }

        /// <summary>The faults as the <c>activeFaults</c> feedback carries them.</summary>
        public static string FaultsJson(TesiraFaultList list)
        {
            var faults = list.Faults.Select(f => new Dictionary<string, string> { { "id", f.Id }, { "name", f.Name } }).ToList();
            var json = JsonConvert.SerializeObject(faults);
            while (json.Length > MaxValueChars && faults.Count > 0)
            {
                faults.RemoveAt(faults.Count - 1);
                json = JsonConvert.SerializeObject(faults);
            }
            return json;
        }

        public static string Issues(TesiraFaultList list)
        {
            var names = list.Faults.Select(f => f.Name ?? f.Id).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            var text = string.Join("; ", names);
            return text.Length > MaxValueChars ? text.Substring(0, MaxValueChars) : text;
        }
    }
}
