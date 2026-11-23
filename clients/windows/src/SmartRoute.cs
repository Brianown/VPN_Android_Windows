using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteTunnel
{
    /// <summary>
    /// 智能分流：把客户端配置里的 AllowedIPs = 0.0.0.0/0 替换为“非中国 IPv4 段”，
    /// 只让国外 IP 进入 WireGuard。列表来自 APNIC delegation，已把私网/保留地址排除。
    /// </summary>
    public static class SmartRoute
    {
        private static readonly Regex AllowedIpsRegex =
            new Regex(@"^(\s*AllowedIPs\s*=\s*).*$", RegexOptions.Multiline);
        private static readonly Regex StatsUrlRegex =
            new Regex(@"^#\s*StatsURL\s*=\s*https?://([^/:\s]+)", RegexOptions.Multiline);
        private static readonly Regex DnsRegex =
            new Regex(@"^\s*DNS\s*=\s*(.+)$", RegexOptions.Multiline);
        private static readonly Regex EndpointRegex =
            new Regex(@"^\s*Endpoint\s*=\s*([^:\s]+):", RegexOptions.Multiline);
        private static readonly object SyncRoot = new object();
        private static string cachedRoutes;

        public static string BuildSmartConfig(ProfileInfo profile, string originalConfig)
        {
            string routes = ExcludeEndpoint(LoadRoutes(), originalConfig);
            Match statsMatch = StatsUrlRegex.Match(originalConfig);
            string extra = string.Empty;
            if (statsMatch.Success)
            {
                // 月度流量 API 在 VPN 内网，必须单独保留一条路由。
                extra = statsMatch.Groups[1].Value.Trim() + "/32, ";
            }

            // 关键：智能分流只放行“非中国 IP”，但配置里的 DNS 1.1.1.1/8.8.8.8
            // 不在这个列表里，DNS 会绕过隧道，导致 YouTube 等域名解析失败。
            // 把 DNS 服务器本身也加进 AllowedIPs，确保 DNS 查询走隧道。
            string dnsExtra = BuildDnsRoutes(originalConfig);
            if (dnsExtra.Length > 0)
                extra += dnsExtra + ", ";

            string replaced = AllowedIpsRegex.Replace(originalConfig, delegate(Match match)
            {
                return match.Groups[1].Value + extra + routes;
            }, 1);

            if (string.Equals(replaced, originalConfig, StringComparison.Ordinal))
                throw new InvalidOperationException("配置中找不到 AllowedIPs，无法启用智能分流。");

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string directory = Path.Combine(Path.Combine(appData, "LiteTunnel"), "smart");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, profile.TunnelName + ".conf");
            File.WriteAllText(path, replaced, new UTF8Encoding(false));
            Match endpoint = EndpointRegex.Match(originalConfig);
            Logger.Log("Smart config generated: endpoint="
                + (endpoint.Success ? endpoint.Groups[1].Value : "?")
                + ", routes=" + routes.Split(',').Length + ", path=" + path);
            return path;
        }

        private static string ExcludeEndpoint(string routes, string config)
        {
            Match endpointMatch = EndpointRegex.Match(config);
            if (!endpointMatch.Success)
                return routes;

            System.Net.IPAddress endpoint;
            if (!System.Net.IPAddress.TryParse(endpointMatch.Groups[1].Value.Trim(), out endpoint))
                return routes;
            if (endpoint.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return routes;

            uint endpointValue = ToUInt32(endpoint);
            StringBuilder builder = new StringBuilder();
            foreach (string part in routes.Split(','))
            {
                string cidr = part.Trim();
                if (cidr.Length == 0)
                    continue;

                string[] pieces = cidr.Split('/');
                System.Net.IPAddress network;
                int prefix;
                if (pieces.Length == 2 && System.Net.IPAddress.TryParse(pieces[0], out network) &&
                    int.TryParse(pieces[1], out prefix) &&
                    network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
                    if ((ToUInt32(network) & mask) == (endpointValue & mask))
                    {
                        // 不能把 WireGuard 服务器自身放进隧道，否则握手包会回环。
                        continue;
                    }
                }

                if (builder.Length > 0)
                    builder.Append(", ");
                builder.Append(cidr);
            }
            return builder.ToString();
        }

        private static uint ToUInt32(System.Net.IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                   ((uint)bytes[2] << 8) | bytes[3];
        }
        private static string BuildDnsRoutes(string config)
        {
            Match match = DnsRegex.Match(config);
            if (!match.Success)
                return string.Empty;

            StringBuilder builder = new StringBuilder();
            foreach (string part in match.Groups[1].Value.Split(','))
            {
                string value = part.Trim();
                if (value.Length == 0)
                    continue;
                System.Net.IPAddress address;
                if (!System.Net.IPAddress.TryParse(value, out address))
                    continue;
                if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    continue;
                if (builder.Length > 0)
                    builder.Append(", ");
                builder.Append(value).Append("/32");
            }
            return builder.ToString();
        }

        private static string LoadRoutes()
        {
            lock (SyncRoot)
            {
                if (cachedRoutes != null)
                    return cachedRoutes;

                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream("non_china_ipv4.txt"))
                {
                    if (stream == null)
                        throw new InvalidOperationException("内置智能分流列表丢失。");
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string text = reader.ReadToEnd();
                        StringBuilder builder = new StringBuilder();
                        foreach (string line in text.Split('\n'))
                        {
                            string cidr = line.Trim();
                            if (cidr.Length == 0)
                                continue;
                            if (builder.Length > 0)
                                builder.Append(", ");
                            builder.Append(cidr);
                        }
                        cachedRoutes = builder.ToString();
                    }
                }
                return cachedRoutes;
            }
        }
    }
}
