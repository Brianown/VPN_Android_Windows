using System;
using System.IO;
using System.Text;

namespace LiteTunnel
{
    /// <summary>轻量日志：%APPDATA%\LiteTunnel\logs\litetunnel.log</summary>
    public static class Logger
    {
        private static readonly object SyncRoot = new object();

        public static string LogPath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(Path.Combine(Path.Combine(appData, "LiteTunnel"), "logs"), "litetunnel.log");
            }
        }

        public static void Log(string message)
        {
            try
            {
                lock (SyncRoot)
                {
                    string path = LogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                        File.Delete(path);
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + "\r\n",
                        new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
            }
        }

        public static void Open()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                if (!File.Exists(LogPath))
                    File.WriteAllText(LogPath, string.Empty, new UTF8Encoding(false));
                System.Diagnostics.Process.Start("notepad.exe", "\"" + LogPath + "\"");
            }
            catch (Exception)
            {
            }
        }
    }
}
