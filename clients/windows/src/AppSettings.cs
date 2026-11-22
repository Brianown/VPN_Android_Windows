using System;
using System.IO;
using System.Text;

namespace LiteTunnel
{
    /// <summary>非常轻量的本地设置（%APPDATA%\LiteTunnel\settings.ini）。</summary>
    public static class AppSettings
    {
        private static string SettingsPath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(Path.Combine(appData, "LiteTunnel"), "settings.ini");
            }
        }

        public static bool SmartMode
        {
            get { return Read("smart_mode", "0") == "1"; }
            set { Write("smart_mode", value ? "1" : "0"); }
        }

        private static string Read(string key, string fallback)
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return fallback;
                foreach (string line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    int index = line.IndexOf('=');
                    if (index <= 0)
                        continue;
                    if (line.Substring(0, index).Trim() == key)
                        return line.Substring(index + 1).Trim();
                }
            }
            catch (Exception)
            {
            }
            return fallback;
        }

        private static void Write(string key, string value)
        {
            try
            {
                string path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, key + "=" + value + "\r\n",
                    new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }
    }
}
