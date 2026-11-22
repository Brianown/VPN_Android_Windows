using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;

namespace LiteTunnel
{
    /// <summary>
    /// 调用官方开源的 WireGuard for Windows（wireguard.exe）管理隧道服务。
    /// 隧道服务名为 WireGuardTunnel$&lt;隧道名&gt;。
    /// </summary>
    public static class WireGuardManager
    {
        private const string ServicePrefix = "WireGuardTunnel$";

        public static string FindWireGuardExe()
        {
            List<string> candidates = new List<string>();
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFiles))
                candidates.Add(Path.Combine(programFiles, "WireGuard", "wireguard.exe"));
            if (!string.IsNullOrEmpty(programFilesX86))
                candidates.Add(Path.Combine(programFilesX86, "WireGuard", "wireguard.exe"));

            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string dir in path.Split(Path.PathSeparator))
                {
                    try
                    {
                        if (dir.Trim().Length == 0)
                            continue;
                        candidates.Add(Path.Combine(dir.Trim(), "wireguard.exe"));
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        public static bool IsInstalled
        {
            get { return FindWireGuardExe() != null; }
        }

        public static List<string> GetRunningTunnelNames()
        {
            List<string> result = new List<string>();
            try
            {
                ServiceController[] services = ServiceController.GetServices();
                foreach (ServiceController service in services)
                {
                    try
                    {
                        if (service.ServiceName.StartsWith(ServicePrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            if (service.Status == ServiceControllerStatus.Running)
                                result.Add(service.ServiceName.Substring(ServicePrefix.Length));
                        }
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        service.Dispose();
                    }
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        public static bool IsTunnelRunning(string tunnelName)
        {
            try
            {
                using (ServiceController service =
                    new ServiceController(ServicePrefix + tunnelName))
                {
                    return service.Status == ServiceControllerStatus.Running;
                }
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        public static void StartTunnel(ProfileInfo profile)
        {
            StartTunnel(profile.FilePath, profile.TunnelName);
        }

        public static void StartTunnel(string configPath, string tunnelName)
        {
            string exe = FindWireGuardExe();
            if (exe == null)
                throw new InvalidOperationException("未检测到 WireGuard for Windows，请先安装。");

            string output;
            int code = Run(exe, "/installtunnelservice \"" + configPath + "\"", 20000, out output);
            if (code != 0)
                throw new Exception("创建 WireGuard 隧道失败（退出码 " + code + "）。\r\n" + output);

            if (!WaitForService(tunnelName, true, 12000))
                throw new Exception("WireGuard 隧道服务未能在 12 秒内启动，请检查配置或 Windows 事件查看器。");
        }

        public static void StopTunnel(string tunnelName)
        {
            string exe = FindWireGuardExe();
            if (exe == null)
                throw new InvalidOperationException("未检测到 WireGuard for Windows。");

            string output;
            int code = Run(exe, "/uninstalltunnelservice \"" + tunnelName + "\"", 20000, out output);
            if (code != 0)
                throw new Exception("断开 WireGuard 隧道失败（退出码 " + code + "）。\r\n" + output);

            WaitForService(tunnelName, false, 8000);
        }

        private static bool WaitForService(string tunnelName, bool running, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (IsTunnelRunning(tunnelName) == running)
                    return true;
                Thread.Sleep(350);
            }
            return IsTunnelRunning(tunnelName) == running;
        }

        private static int Run(string exe, string arguments, int timeoutMs, out string output)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(exe, arguments);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.WorkingDirectory = Path.GetDirectoryName(exe);

            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    output = "无法启动 wireguard.exe。";
                    return -1;
                }
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception)
                    {
                    }
                    throw new System.TimeoutException("WireGuard 操作超时。");
                }
                output = (stdout + "\r\n" + stderr).Trim();
                return process.ExitCode;
            }
        }
    }
}
