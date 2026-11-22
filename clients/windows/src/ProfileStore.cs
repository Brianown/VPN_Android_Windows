using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteTunnel
{
    public sealed class ProfileInfo
    {
        public string FilePath { get; set; }
        public string TunnelName { get; set; }
        public string DisplayName { get; set; }
        public string Endpoint { get; set; }
    }

    /// <summary>
    /// 配置文件管理：每个 WireGuard .conf 文件保存在 %APPDATA%\LiteTunnel\profiles 下。
    /// 文件名就是 WireGuard 隧道名；显示名称保存在配置顶部的 "# Name = ..." 注释中。
    /// </summary>
    public static class ProfileStore
    {
        private static readonly Regex NameRegex =
            new Regex(@"^#\s*Name\s*[:=]\s*(.+)$", RegexOptions.Multiline);
        private static readonly Regex EndpointRegex =
            new Regex(@"^\s*Endpoint\s*=\s*(\S+)", RegexOptions.Multiline);
        private static readonly Regex StatsUrlRegex =
            new Regex(@"^#\s*StatsURL\s*=\s*(\S+)", RegexOptions.Multiline);
        private static readonly Regex StatsTokenRegex =
            new Regex(@"^#\s*StatsToken\s*=\s*(\S+)", RegexOptions.Multiline);
        private static readonly Regex LocationRegex =
            new Regex(@"^#\s*Location\s*=\s*(.+)$", RegexOptions.Multiline);
        private static readonly Regex ServerTypeRegex =
            new Regex(@"^#\s*ServerType\s*=\s*(.+)$", RegexOptions.Multiline);
        private static readonly Regex ProbePortRegex =
            new Regex(@"^#\s*ProbePort\s*=\s*(\d+)\s*$", RegexOptions.Multiline);
        private static readonly Regex InterfaceRegex =
            new Regex(@"^\s*\[Interface\]\s*$", RegexOptions.Multiline);
        private static readonly Regex PeerRegex =
            new Regex(@"^\s*\[Peer\]\s*$", RegexOptions.Multiline);
        private static readonly Regex PrivateKeyRegex =
            new Regex(@"^\s*PrivateKey\s*=\s*\S+", RegexOptions.Multiline);
        private static readonly Regex PublicKeyRegex =
            new Regex(@"^\s*PublicKey\s*=\s*\S+", RegexOptions.Multiline);
        private static readonly Regex AllowedIpsRegex =
            new Regex(@"^\s*AllowedIPs\s*=\s*\S+", RegexOptions.Multiline);

        public static string ProfilesDir
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(Path.Combine(appData, "LiteTunnel"), "profiles");
            }
        }

        public static void EnsureDirectory()
        {
            Directory.CreateDirectory(ProfilesDir);
        }

        public static List<ProfileInfo> Load()
        {
            List<ProfileInfo> result = new List<ProfileInfo>();
            EnsureDirectory();
            string[] files;
            try
            {
                files = Directory.GetFiles(ProfilesDir, "*.conf");
            }
            catch (Exception)
            {
                return result;
            }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                try
                {
                    string text = File.ReadAllText(file, Encoding.UTF8);
                    string display = GetDisplayName(text);
                    if (string.IsNullOrEmpty(display))
                        display = Path.GetFileNameWithoutExtension(file);
                    string endpoint = GetEndpoint(text);
                    if (string.IsNullOrEmpty(endpoint))
                        endpoint = "未配置 Endpoint";

                    ProfileInfo info = new ProfileInfo();
                    info.FilePath = file;
                    info.TunnelName = Path.GetFileNameWithoutExtension(file);
                    info.DisplayName = display;
                    info.Endpoint = endpoint;
                    result.Add(info);
                }
                catch (Exception)
                {
                    // 忽略无法读取的文件
                }
            }
            return result;
        }

        public static ProfileInfo Import(string sourcePath, out string error)
        {
            error = null;
            string text;
            try
            {
                text = File.ReadAllText(sourcePath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
            if (!ValidateConfig(text, out error))
                return null;

            string display = GetDisplayName(text);
            if (string.IsNullOrEmpty(display))
                display = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrEmpty(display))
                display = "节点";

            string fileBase = SanitizeTunnelName(Path.GetFileNameWithoutExtension(sourcePath));
            string tunnelName = EnsureUniqueTunnelName(fileBase);
            EnsureDirectory();
            string target = Path.Combine(ProfilesDir, tunnelName + ".conf");
            text = WithNameComment(text, display);
            try
            {
                File.WriteAllText(target, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                error = "写入配置文件失败：" + ex.Message;
                return null;
            }
            return ToInfo(target, text);
        }

        public static ProfileInfo Create(string displayName, string configText, out string error)
        {
            error = null;
            displayName = (displayName ?? string.Empty).Trim()
                .Replace("\r", " ").Replace("\n", " ");
            if (displayName.Length == 0)
            {
                error = "请输入节点名称。";
                return null;
            }
            if (!ValidateConfig(configText, out error))
                return null;

            string tunnelName = EnsureUniqueTunnelName(displayName);
            EnsureDirectory();
            string target = Path.Combine(ProfilesDir, tunnelName + ".conf");
            string text = WithNameComment(configText.Trim(), displayName);
            try
            {
                File.WriteAllText(target, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                error = "写入配置文件失败：" + ex.Message;
                return null;
            }
            return ToInfo(target, text);
        }

        public static bool ValidateConfig(string text, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "配置内容为空。";
                return false;
            }
            string normalized = text.Replace("\r\n", "\n");
            if (!InterfaceRegex.IsMatch(normalized))
            {
                error = "缺少 [Interface] 段。";
                return false;
            }
            if (!PrivateKeyRegex.IsMatch(normalized))
            {
                error = "[Interface] 中缺少 PrivateKey。";
                return false;
            }
            if (!PeerRegex.IsMatch(normalized))
            {
                error = "缺少 [Peer] 段。";
                return false;
            }
            if (!PublicKeyRegex.IsMatch(normalized))
            {
                error = "[Peer] 中缺少 PublicKey。";
                return false;
            }
            if (!EndpointRegex.IsMatch(normalized))
            {
                error = "[Peer] 中缺少 Endpoint（服务器地址）。";
                return false;
            }
            if (!AllowedIpsRegex.IsMatch(normalized))
            {
                error = "[Peer] 中缺少 AllowedIPs。";
                return false;
            }
            return true;
        }

        public static string GetDisplayName(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            Match match = NameRegex.Match(text);
            if (!match.Success)
                return null;
            string name = match.Groups[1].Value.Trim();
            return name.Length == 0 ? null : name;
        }

        public static string GetEndpoint(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            Match match = EndpointRegex.Match(text);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        public static string GetStatsUrl(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            Match match = StatsUrlRegex.Match(text);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        public static string GetStatsToken(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            Match match = StatsTokenRegex.Match(text);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        public static string GetServerHost(string text)
        {
            string endpoint = GetEndpoint(text);
            if (string.IsNullOrEmpty(endpoint))
                return null;
            if (endpoint.StartsWith("["))
            {
                int end = endpoint.IndexOf(']');
                return end > 1 ? endpoint.Substring(1, end - 1) : null;
            }
            int colon = endpoint.LastIndexOf(':');
            return colon > 0 ? endpoint.Substring(0, colon) : endpoint;
        }

        public static string GetLocation(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "未知";
            Match match = LocationRegex.Match(text);
            if (!match.Success)
                return "未知";
            string location = match.Groups[1].Value.Trim();
            return location.Length == 0 ? "未知" : location;
        }

        public static string GetServerType(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "未知";
            Match match = ServerTypeRegex.Match(text);
            if (!match.Success)
                return "未知";
            string type = match.Groups[1].Value.Trim();
            return type.Length == 0 ? "未知" : type;
        }

        public static int GetProbePort(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            Match match = ProbePortRegex.Match(text);
            int port;
            if (match.Success && int.TryParse(match.Groups[1].Value, out port) && port > 0 && port <= 65535)
                return port;
            return 0;
        }

        public static string SanitizeTunnelName(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "tunnel";
            StringBuilder builder = new StringBuilder();
            foreach (char c in raw)
            {
                bool valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '=' || c == '+'
                    || c == '.' || c == '-';
                builder.Append(valid ? c : '_');
            }
            string name = builder.ToString().Trim('.', '-', '_', '=', '+');
            if (name.Length == 0)
                name = "tunnel";
            if (name.Length > 15)
                name = name.Substring(0, 15);
            return name;
        }

        private static string EnsureUniqueTunnelName(string raw)
        {
            string baseName = SanitizeTunnelName(raw);
            if (baseName.Length > 12)
                baseName = baseName.Substring(0, 12);
            string candidate = baseName;
            int suffix = 2;
            while (File.Exists(Path.Combine(ProfilesDir, candidate + ".conf")))
            {
                candidate = baseName + "-" + suffix;
                suffix++;
            }
            return candidate;
        }

        private static string WithNameComment(string text, string displayName)
        {
            string trimmed = (text ?? string.Empty).Trim();
            if (NameRegex.IsMatch(trimmed))
            {
                return NameRegex.Replace(trimmed, delegate(Match match)
                {
                    return "# Name = " + displayName;
                }, 1);
            }
            return "# Name = " + displayName + "\r\n" + trimmed;
        }

        private static ProfileInfo ToInfo(string path, string text)
        {
            ProfileInfo info = new ProfileInfo();
            info.FilePath = path;
            info.TunnelName = Path.GetFileNameWithoutExtension(path);
            string display = GetDisplayName(text);
            info.DisplayName = string.IsNullOrEmpty(display)
                ? Path.GetFileNameWithoutExtension(path) : display;
            string endpoint = GetEndpoint(text);
            info.Endpoint = string.IsNullOrEmpty(endpoint) ? "未配置 Endpoint" : endpoint;
            return info;
        }
    }
}
