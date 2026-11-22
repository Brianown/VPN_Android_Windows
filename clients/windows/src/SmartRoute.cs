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
        private static readonly object SyncRoot = new object();
        private static string cachedRoutes;

        public static string BuildSmartConfig(ProfileInfo profile, string originalConfig)
        {
            string routes = LoadRoutes();
            Match statsMatch = StatsUrlRegex.Match(originalConfig);
            string extra = string.Empty;
            if (statsMatch.Success)
            {
                // 月度流量 API 在 VPN 内网，必须单独保留一条路由。
                extra = statsMatch.Groups[1].Value.Trim() + "/32, ";
            }

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
            return path;
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
