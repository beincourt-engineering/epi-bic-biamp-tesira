using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;

// The plugin builds without nullable annotations; the test project links this file and has them on.
#nullable disable

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira
{
    /// <summary>One network interface on the DSP: the control port, a Dante port or the VoIP card.</summary>
    public sealed class TesiraNic
    {
        /// <summary>control, dante primary, dante secondary or voip (with its block's tag when there are two).</summary>
        [JsonProperty("name")] public string Name { get; set; }

        [JsonProperty("mac", NullValueHandling = NullValueHandling.Ignore)] public string Mac { get; set; }

        /// <summary>True when the port has a link, false when it has none, null when the DSP doesn't say.</summary>
        [JsonProperty("linkUp", NullValueHandling = NullValueHandling.Ignore)] public bool? LinkUp { get; set; }

        [JsonProperty("linkMbps", NullValueHandling = NullValueHandling.Ignore)] public int? LinkMbps { get; set; }

        /// <summary>The link as the DSP names it (1 Gb, 1000 Mb/s), or the code a non-verbose reply sent.</summary>
        [JsonProperty("link", NullValueHandling = NullValueHandling.Ignore)] public string Link { get; set; }

        /// <summary>DHCP or static, or the code a non-verbose reply sent.</summary>
        [JsonProperty("addressing", NullValueHandling = NullValueHandling.Ignore)] public string Addressing { get; set; }

        [JsonProperty("ip", NullValueHandling = NullValueHandling.Ignore)] public string Ip { get; set; }
        [JsonProperty("netmask", NullValueHandling = NullValueHandling.Ignore)] public string Netmask { get; set; }
        [JsonProperty("gateway", NullValueHandling = NullValueHandling.Ignore)] public string Gateway { get; set; }
        [JsonProperty("dns", NullValueHandling = NullValueHandling.Ignore)] public List<string> Dns { get; set; }
        [JsonProperty("hostname", NullValueHandling = NullValueHandling.Ignore)] public string Hostname { get; set; }
        [JsonProperty("dhcpLeaseObtained", NullValueHandling = NullValueHandling.Ignore)] public string DhcpLeaseObtained { get; set; }
        [JsonProperty("dhcpLeaseExpires", NullValueHandling = NullValueHandling.Ignore)] public string DhcpLeaseExpires { get; set; }

        /// <summary>The VLAN the port is on, when it uses one.</summary>
        [JsonProperty("vlan", NullValueHandling = NullValueHandling.Ignore)] public int? Vlan { get; set; }

        /// <summary>The port's Ethernet setting (VoIP card), e.g. auto, full duplex.</summary>
        [JsonProperty("ethernet", NullValueHandling = NullValueHandling.Ignore)] public string Ethernet { get; set; }
    }

    /// <summary>
    /// Reads every network interface the DSP reports: the control port(s) from <c>DEVICE get networkStatus</c>,
    /// the Dante ports from <c>DEVICE get danteInfo</c> and the VoIP card from its control/status block's
    /// <c>network</c> attribute. Verbose and non-verbose replies are both read. Free of Crestron types.
    /// </summary>
    /// <remarks>
    /// A non-verbose reply sends an enum as a number and Biamp publishes no table of the numbers, so only
    /// the ones seen on a unit are named (link 4 = 1 Gb, address source 2 = DHCP, VoIP Ethernet speed 3 = auto
    /// and duplex 2 = full); any other is kept as "code N" rather than guessed.
    /// </remarks>
    public static class TesiraNicParser
    {
        /// <summary>The feedback's longest value, so a reader that limits values to 2048 never gets broken JSON.</summary>
        public const int MaxValueChars = 2000;

        private static readonly System.Text.RegularExpressions.Regex MacShape =
            new System.Text.RegularExpressions.Regex("^[0-9A-Fa-f]{2}([:-][0-9A-Fa-f]{2}){5}$");

        private static readonly Dictionary<string, string> LinkCodes = new Dictionary<string, string> { { "4", "LINK_1_GB" } };
        private static readonly Dictionary<string, string> AddressCodes = new Dictionary<string, string> { { "2", "DHCP" } };
        private static readonly Dictionary<string, string> VoipSpeedCodes = new Dictionary<string, string> { { "3", "VOIP_ETH_AUTO" } };
        private static readonly Dictionary<string, string> VoipDuplexCodes = new Dictionary<string, string> { { "2", "VOIP_ETH_FULL" } };

        /// <summary>The control port(s) in a networkStatus reply, control first. Empty when it isn't one.</summary>
        public static List<TesiraNic> ControlNics(string networkStatusReply)
        {
            var nics = new List<TesiraNic>();
            var root = TtpValue.Parse(networkStatusReply);
            if (root == null || !root.IsContainer) return nics;

            var interfaces = root.Get("networkInterfaceStatusWithName", 3);
            if (interfaces == null || !interfaces.IsContainer) return nics;

            var dnsNode = root.Get("dnsStatus", 4);
            var dns = dnsNode != null && dnsNode.IsContainer
                ? Addresses(new[] { dnsNode.Get("primaryDNSServer", 0), dnsNode.Get("secondaryDNSServer", 1) })
                : null;

            foreach (var entry in interfaces.Items)
            {
                if (!entry.IsContainer) continue;
                var status = entry.Get("networkInterfaceStatus", 1);
                if (status == null || !status.IsContainer) continue;

                var linkWord = EnumWord(status.Get("linkStatus", 1), LinkCodes);
                nics.Add(new TesiraNic
                {
                    Name = entry.Get("interfaceId", 0)?.AsText() ?? "control",
                    Mac = Blank(status.Get("macAddress", 0)?.AsText()),
                    Link = LinkText(linkWord),
                    LinkMbps = LinkMbps(linkWord),
                    LinkUp = LinkUp(linkWord),
                    Addressing = AddressText(EnumWord(status.Get("addressSource", 2), AddressCodes)),
                    Ip = Address(status.Get("ip", 3)?.AsText()),
                    Netmask = Address(status.Get("netmask", 4)?.AsText()),
                    DhcpLeaseObtained = Blank(status.Get("dhcpLeaseObtainedDate", 5)?.AsText()),
                    DhcpLeaseExpires = Blank(status.Get("dhcpLeaseExpiresDate", 6)?.AsText()),
                    Gateway = Address(status.Get("gateway", 7)?.AsText()),
                    Dns = dns,
                });
            }

            return nics;
        }

        /// <summary>The DSP's host name in a networkStatus reply, or null.</summary>
        public static string Hostname(string networkStatusReply)
        {
            var root = TtpValue.Parse(networkStatusReply);
            return root == null || !root.IsContainer ? null : Blank(root.Get("hostname", 1)?.AsText());
        }

        /// <summary>
        /// The Dante ports in a danteInfo reply: primary then secondary, each with the address settings from
        /// netCfg and the MAC and link speed from allNetStatus. Empty when the reply isn't one.
        /// </summary>
        public static List<TesiraNic> DanteNics(string danteInfoReply)
        {
            var nics = new List<TesiraNic>();
            var root = TtpValue.Parse(danteInfoReply);
            if (root == null || !root.IsContainer) return nics;

            var netCfg = root.Get("netCfg", 1);
            var netStatus = root.Get("allNetStatus", 2);
            if (netCfg == null || !netCfg.IsContainer || netStatus == null || !netStatus.IsContainer) return nics;

            var hostname = Blank(netCfg.Get("hostname", 0)?.AsText());
            var ports = new[] { new[] { "primaryInterface", "primaryStatus", "dante primary" }, new[] { "secondaryInterface", "secondaryStatus", "dante secondary" } };
            for (var i = 0; i < ports.Length; i++)
            {
                var cfg = netCfg.Get(ports[i][0], i + 1);
                var status = netStatus.Get(ports[i][1], i);
                if ((cfg == null || !cfg.IsContainer) && (status == null || !status.IsContainer)) continue;

                var speed = status != null && status.IsContainer ? status.Get("linkSpeedInMbS", 1)?.AsInt() : null;
                var auto = cfg != null && cfg.IsContainer ? cfg.Get("autoConfigure", 0)?.AsBool() : null;
                nics.Add(new TesiraNic
                {
                    Name = ports[i][2],
                    Hostname = hostname,
                    Mac = status != null && status.IsContainer ? Blank(status.Get("macAddress", 0)?.AsText()) : null,
                    LinkMbps = speed > 0 ? speed : null,
                    LinkUp = speed == null ? (bool?)null : speed > 0,
                    Link = speed == null ? null : speed > 0 ? speed.Value.ToString(CultureInfo.InvariantCulture) + " Mb/s" : "down",
                    Addressing = auto == null ? null : auto.Value ? "DHCP" : "static",
                    Ip = cfg != null && cfg.IsContainer ? Address(cfg.Get("ipAddress", 1)?.AsText()) : null,
                    Netmask = cfg != null && cfg.IsContainer ? Address(cfg.Get("netMask", 2)?.AsText()) : null,
                    Dns = cfg != null && cfg.IsContainer ? Addresses(new[] { cfg.Get("DNSServer", 3) }) : null,
                    Gateway = cfg != null && cfg.IsContainer ? Address(cfg.Get("defaultGateway", 4)?.AsText()) : null,
                });
            }

            return nics;
        }

        /// <summary>
        /// The VoIP card's interface from its control/status block's <c>network</c> reply. Its addresses come as
        /// whole numbers (the address's bytes, lowest first; 0 is none). Null when the reply isn't one.
        /// </summary>
        public static TesiraNic VoipNic(string networkReply, string name)
        {
            var root = TtpValue.Parse(networkReply);
            if (root == null || !root.IsContainer) return null;

            var general = root.Get("general", 0);
            if (general == null || !general.IsContainer) return null;

            var mac = Blank(general.Get("mac", 0)?.AsText());
            if (mac == null || !MacShape.IsMatch(mac)) return null;

            var vlan = root.Get("vlan", 1);
            var eth = root.Get("ethnet", 2);
            var dhcp = general.Get("dhcpEnable", 1)?.AsBool();
            var vlanOn = vlan != null && vlan.IsContainer ? vlan.Get("vlanEnable", 0)?.AsBool() : null;

            var speed = eth != null && eth.IsContainer ? EnumWord(eth.Get("ethSpeed", 0), VoipSpeedCodes) : null;
            var duplex = eth != null && eth.IsContainer ? EnumWord(eth.Get("ethDuplex", 1), VoipDuplexCodes) : null;

            return new TesiraNic
            {
                Name = name,
                Mac = mac,
                Addressing = dhcp == null ? null : dhcp.Value ? "DHCP" : "static",
                Ip = NumberAddress(general.Get("addr", 3)),
                Netmask = NumberAddress(general.Get("netmask", 4)),
                Gateway = NumberAddress(general.Get("gateway", 5)),
                Dns = NumberAddresses(new[] { general.Get("dnsPrimary", 6), general.Get("dnsSecondary", 7) }),
                Vlan = vlanOn == true ? vlan.Get("vlanId", 1)?.AsInt() : null,
                Ethernet = EthernetText(speed, duplex),
            };
        }

        /// <summary>
        /// The <c>network</c> feedback: the DSP's host name and every interface, as JSON. Cut to the control
        /// port alone if it would pass <see cref="MaxValueChars"/>.
        /// </summary>
        public static string NetworkJson(string hostname, IEnumerable<TesiraNic> nics)
        {
            var list = (nics ?? Enumerable.Empty<TesiraNic>()).Where(n => n != null).ToList();
            var json = JsonConvert.SerializeObject(new { hostname, interfaces = list });
            if (json.Length > MaxValueChars)
            {
                json = JsonConvert.SerializeObject(new { hostname, interfaces = list.Take(1).ToList(), cut = true });
            }

            return json;
        }

        /// <summary>A whole-number address as dotted text, its lowest byte first. 0 and anything else unusable is null.</summary>
        public static string NumberAddress(TtpNode node)
        {
            var text = node?.AsText();
            long n;
            if (text == null || !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n == 0)
            {
                return null;
            }

            var v = unchecked((uint)n);
            return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}", v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF, (v >> 24) & 0xFF);
        }

        private static List<string> NumberAddresses(IEnumerable<TtpNode> nodes)
        {
            var list = nodes.Select(NumberAddress).Where(a => a != null).ToList();
            return list.Count == 0 ? null : list;
        }

        private static List<string> Addresses(IEnumerable<TtpNode> nodes)
        {
            var list = nodes.Select(n => Address(n?.AsText())).Where(a => a != null).ToList();
            return list.Count == 0 ? null : list;
        }

        /// <summary>An address, or null for a blank or 0.0.0.0 (none).</summary>
        private static string Address(string text)
        {
            text = Blank(text);
            return text == null || text == "0.0.0.0" ? null : text;
        }

        private static string Blank(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        /// <summary>An enum's word: as sent when verbose, from the known numbers when not, else "code N".</summary>
        private static string EnumWord(TtpNode node, Dictionary<string, string> codes)
        {
            var text = Blank(node?.AsText());
            if (text == null) return null;
            if (text.All(char.IsDigit))
            {
                string word;
                return codes.TryGetValue(text, out word) ? word : "code " + text;
            }

            return text;
        }

        private static int? LinkMbps(string word)
        {
            if (word == null || !word.StartsWith("LINK_", StringComparison.Ordinal)) return null;
            var parts = word.Split('_');
            int n;
            if (parts.Length != 3 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return null;
            return parts[2] == "GB" ? n * 1000 : parts[2] == "MB" ? n : (int?)null;
        }

        private static bool? LinkUp(string word)
        {
            if (word == null || word.StartsWith("code ", StringComparison.Ordinal)) return null;
            if (word == "LINK_DOWN" || word == "LINK_NONE") return false;
            return word.StartsWith("LINK_", StringComparison.Ordinal) ? true : (bool?)null;
        }

        private static string LinkText(string word)
        {
            if (word == null) return null;
            var mbps = LinkMbps(word);
            if (mbps != null) return mbps >= 1000 && mbps % 1000 == 0 ? (mbps / 1000) + " Gb" : mbps + " Mb";
            if (word == "LINK_DOWN" || word == "LINK_NONE") return "down";
            if (word == "LINK_UP") return "up";
            return word;
        }

        private static string AddressText(string word)
        {
            if (word == null) return null;
            if (word == "DHCP") return "DHCP";
            if (word == "STATIC") return "static";
            return word;
        }

        private static string EthernetText(string speed, string duplex)
        {
            var parts = new List<string>();
            if (speed != null) parts.Add(speed == "VOIP_ETH_AUTO" ? "auto" : speed.Replace("VOIP_ETH_", "").ToLowerInvariant());
            if (duplex != null) parts.Add(duplex == "VOIP_ETH_FULL" ? "full duplex" : duplex == "VOIP_ETH_HALF" ? "half duplex" : duplex.Replace("VOIP_ETH_", "").ToLowerInvariant());
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }
}
