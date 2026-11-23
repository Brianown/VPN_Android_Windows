using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text;
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
        private const string WireGuardMsiUrl =
            "https://github.com/Brianown/VPN_Android_Windows/raw/main/release/WireGuard-amd64-1.1.1.msi";
        private const string WireGuardMsiResource = "WireGuard-amd64-1.1.1.msi";
        private const string WireGuardMsiSha256 =
            "7bfed60ad61b785c914b38b61555a975488e1d3ec472dbfb2fcdf498fca75242";

        public static string FindWireGuardExe()
        {
            List<string> candidates = new List<string>();
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(programFiles))
                candidates.Add(Path.Combine(programFiles, "WireGuard", "wireguard.exe"));
            if (!string.IsNullOrEmpty(programFilesX86))
                candidates.Add(Path.Combine(programFilesX86, "WireGuard", "wireguard.exe"));
            if (!string.IsNullOrEmpty(localAppData))
                candidates.Add(Path.Combine(localAppData, "Programs", "WireGuard", "wireguard.exe"));

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

        /// <summary>
        /// 优先从项目 GitHub 仓库下载官方 MSI（不依赖无法访问的 wireguard.com），
        /// 失败后再尝试 winget。需要管理员权限。
        /// </summary>
        public static bool InstallWireGuard(out string message)
        {
            string msiMessage;
            if (TryInstallFromGithubMsi(out msiMessage))
            {
                message = string.Empty;
                return true;
            }

            string wingetMessage;
            if (TryInstallWithWinget(out wingetMessage))
            {
                message = string.Empty;
                return true;
            }

            message = "自动安装 WireGuard 失败。\r\n\r\n"
                + msiMessage + "\r\n\r\n" + wingetMessage;
            return false;
        }

        private static bool TryInstallFromGithubMsi(out string message)
        {
            message = string.Empty;
            string directory = Path.Combine(Path.GetTempPath(), "LiteTunnel-WireGuard");
            string msi = Path.Combine(directory, "WireGuard-amd64-1.1.1.msi");
            try
            {
                Directory.CreateDirectory(directory);
                if (!File.Exists(msi) || !HasExpectedHash(msi))
                {
                    if (File.Exists(msi))
                        File.Delete(msi);

                    // EXE 内嵌官方 MSI，优先离线释放；没有内嵌资源时再从 GitHub 下载。
                    bool extracted = false;
                    using (Stream resource = Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream(WireGuardMsiResource))
                    {
                        if (resource != null)
                        {
                            using (FileStream fileStream = File.Create(msi))
                            {
                                resource.CopyTo(fileStream);
                            }
                            extracted = true;
                        }
                    }

                    if (!extracted)
                    {
                        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                        using (WebClient client = new WebClient())
                        {
                            client.Headers.Add("User-Agent", "LiteTunnel");
                            client.DownloadFile(WireGuardMsiUrl, msi);
                        }
                    }
                }

                if (!HasExpectedHash(msi))
                {
                    message = "下载到的 WireGuard 安装包 SHA-256 校验失败，已拒绝安装。";
                    try { File.Delete(msi); } catch (Exception) { }
                    return false;
                }

                // 双重检查 Authenticode 签名者，防止仓库被替换成其它 MSI。
                try
                {
                    X509Certificate certificate = X509Certificate.CreateFromSignedFile(msi);
                    if (certificate == null ||
                        certificate.Subject.IndexOf("WireGuard LLC", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        message = "WireGuard 安装包签名者不是 WireGuard LLC，已拒绝安装。";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    message = "无法验证 WireGuard 安装包签名：" + ex.Message;
                    return false;
                }

                string msiexec = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
                string output;
                int code = Run(msiexec,
                    "/i \"" + msi + "\" /qn /norestart REBOOT=ReallySuppress DO_NOT_LAUNCH=1",
                    600000, out output);

                if (WaitForWireGuardInstalled(15))
                {
                    try { File.Delete(msi); } catch (Exception) { }
                    return true;
                }

                message = "MSI 安装失败（退出码 " + code + "）。\r\n" + TrimOutput(output);
                return false;
            }
            catch (Exception ex)
            {
                message = "从 GitHub 下载/安装 WireGuard 失败：" + ex.Message;
                return false;
            }
        }

        private static bool TryInstallWithWinget(out string message)
        {
            message = string.Empty;
            string winget = FindWinget();
            if (string.IsNullOrEmpty(winget))
            {
                message = "未找到 winget。";
                return false;
            }

            try
            {
                string output;
                int code = Run(winget,
                    "install --id WireGuard.WireGuard -e --silent --scope machine " +
                    "--accept-package-agreements --accept-source-agreements --disable-interactivity",
                    600000, out output);

                if (WaitForWireGuardInstalled(10))
                    return true;

                message = "winget 安装失败（退出码 " + code + "）。\r\n" + TrimOutput(output);
                return false;
            }
            catch (Exception ex)
            {
                message = "winget 安装失败：" + ex.Message;
                return false;
            }
        }

        private static bool WaitForWireGuardInstalled(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                if (FindWireGuardExe() != null)
                    return true;
                Thread.Sleep(1000);
            }
            return FindWireGuardExe() != null;
        }

        private static bool HasExpectedHash(string path)
        {
            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    StringBuilder builder = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash)
                        builder.Append(b.ToString("x2"));
                    return string.Equals(builder.ToString(), WireGuardMsiSha256,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string FindWinget()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                string candidate = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
                if (File.Exists(candidate))
                    return candidate;
            }

            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string dir in path.Split(Path.PathSeparator))
                {
                    try
                    {
                        if (dir.Trim().Length == 0)
                            continue;
                        string candidate = Path.Combine(dir.Trim(), "winget.exe");
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            return null;
        }

        private static string TrimOutput(string output)
        {
            if (string.IsNullOrEmpty(output))
                return string.Empty;
            output = output.Trim();
            return output.Length > 2000 ? output.Substring(output.Length - 2000) : output;
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

            if (!WaitForService(tunnelName, true, 30000))
                throw new Exception("WireGuard 隧道服务未能在 30 秒内启动，请检查配置或 Windows 事件查看器。");
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
