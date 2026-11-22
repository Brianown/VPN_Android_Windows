using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace LiteTunnel
{
    public sealed class MainForm : Form
    {
        private static readonly Color Brand = Color.FromArgb(31, 111, 235);
        private static readonly Color BrandDark = Color.FromArgb(11, 79, 207);
        private static readonly Color Ink = Color.FromArgb(15, 23, 42);
        private static readonly Color Muted = Color.FromArgb(100, 116, 139);
        private static readonly Color Line = Color.FromArgb(226, 232, 240);
        private static readonly Color Canvas = Color.FromArgb(241, 245, 249);
        private static readonly Color Success = Color.FromArgb(34, 197, 94);

        private ListView profileList;
        private Label emptyLabel;
        private Label statusTitle;
        private Label statusSubtitle;
        private Label monthlyLabel;
        private Panel statusDot;
        private Button connectButton;
        private Button importButton;
        private Button pasteButton;
        private Button deleteButton;
        private Button openFolderButton;
        private Button aboutButton;
        private CheckBox smartModeCheck;
        private Label footerLabel;
        private LinkLabel installLink;
        private Timer refreshTimer;
        private Timer monthlyTimer;
        private Timer latencyTimer;
        private bool monthlyFetching;
        private string monthlyStatsTunnel;
        private bool runningSmartMode;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private bool allowExit;
        private bool trayHintShown;

        private List<ProfileInfo> profiles = new List<ProfileInfo>();
        private bool busy;
        private Color statusDotColor = Muted;

        public MainForm()
        {
            BuildUi();
            LoadProfiles(null);
            RefreshState();
        }

        private void BuildUi()
        {
            Text = "轻隧 LiteTunnel";
            ClientSize = new Size(880, 640);
            MinimumSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch (Exception)
            {
            }

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildToolbar(), 0, 1);
            root.Controls.Add(BuildContent(), 0, 2);
            root.Controls.Add(BuildFooter(), 0, 3);

            refreshTimer = new Timer();
            refreshTimer.Interval = 1500;
            refreshTimer.Tick += delegate { RefreshState(); };
            refreshTimer.Start();

            monthlyTimer = new Timer();
            monthlyTimer.Interval = 60000;
            monthlyTimer.Tick += delegate { RefreshMonthlyStats(); };

            latencyTimer = new Timer();
            latencyTimer.Interval = 30000;
            latencyTimer.Tick += delegate { MeasureLatencies(); };
            latencyTimer.Start();

            InitializeTray();
        }

        private void InitializeTray()
        {
            trayMenu = new ContextMenuStrip();
            ToolStripMenuItem showItem = new ToolStripMenuItem("显示主界面");
            showItem.Click += delegate { ShowFromTray(); };
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出 LiteTunnel");
            exitItem.Click += delegate { ExitApplication(); };
            trayMenu.Items.Add(showItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(exitItem);

            trayIcon = new NotifyIcon();
            trayIcon.Icon = Icon ?? SystemIcons.Application;
            trayIcon.Text = "轻隧 LiteTunnel";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { ShowFromTray(); };
        }

        private void HideToTray()
        {
            Hide();
            if (!trayHintShown && trayIcon != null)
            {
                trayHintShown = true;
                trayIcon.ShowBalloonTip(3000, "轻隧 LiteTunnel",
                    "程序已隐藏到托盘，VPN 保持连接。右键托盘图标，选择“退出 LiteTunnel”才会真正退出。",
                    ToolTipIcon.Info);
            }
        }

        private void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void ExitApplication()
        {
            if (busy)
                return;

            // 只有托盘菜单里的“退出”才真正结束进程；退出前断开本程序创建的隧道。
            string running = FindActiveTunnelName();
            if (running != null)
            {
                try
                {
                    WireGuardManager.StopTunnel(running);
                }
                catch (Exception)
                {
                }
            }
            allowExit = true;
            if (trayIcon != null)
                trayIcon.Visible = false;
            Close();
        }

        private Control BuildHeader()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = Brand;

            Label title = new Label();
            title.AutoSize = true;
            title.Text = "轻隧 LiteTunnel";
            title.ForeColor = Color.White;
            title.Font = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold, GraphicsUnit.Point);
            title.Location = new Point(24, 14);
            header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.AutoSize = true;
            subtitle.Text = "WireGuard VPN · 简单、快速、开源";
            subtitle.ForeColor = Color.FromArgb(210, 228, 255);
            subtitle.Font = new Font("Microsoft YaHei UI", 9F);
            subtitle.Location = new Point(26, 58);
            header.Controls.Add(subtitle);

            Label version = new Label();
            version.AutoSize = true;
            version.Text = "v1.1.0";
            version.ForeColor = Color.FromArgb(190, 215, 255);
            version.Font = new Font("Microsoft YaHei UI", 9F);
            version.Location = new Point(720, 22);
            header.Controls.Add(version);
            header.Resize += delegate
            {
                version.Location = new Point(header.ClientSize.Width - version.Width - 24, 22);
            };
            return header;
        }

        private Control BuildToolbar()
        {
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = Color.White;
            panel.Padding = new Padding(18, 11, 18, 11);
            panel.WrapContents = false;

            importButton = MakeButton("＋ 导入配置", true);
            pasteButton = MakeButton("粘贴配置", false);
            openFolderButton = MakeButton("配置目录", false);
            deleteButton = MakeButton("删除", false);
            aboutButton = MakeButton("关于", false);

            importButton.Click += delegate { ImportProfiles(); };
            pasteButton.Click += delegate { PasteProfile(); };
            openFolderButton.Click += delegate { OpenProfileFolder(); };
            deleteButton.Click += delegate { DeleteProfile(); };
            aboutButton.Click += delegate { ShowAbout(); };

            panel.Controls.Add(importButton);
            panel.Controls.Add(pasteButton);
            panel.Controls.Add(openFolderButton);
            panel.Controls.Add(deleteButton);
            panel.Controls.Add(aboutButton);

            Button exitButton = MakeButton("退出", false);
            exitButton.Click += delegate { ExitApplication(); };
            panel.Controls.Add(exitButton);

            smartModeCheck = new CheckBox();
            smartModeCheck.Text = "智能分流（国内直连）";
            smartModeCheck.AutoSize = true;
            smartModeCheck.ForeColor = Ink;
            smartModeCheck.Margin = new Padding(14, 6, 0, 0);
            smartModeCheck.Checked = AppSettings.SmartMode;
            smartModeCheck.CheckedChanged += delegate
            {
                AppSettings.SmartMode = smartModeCheck.Checked;
                if (FindActiveTunnelName() != null)
                {
                    MessageBox.Show(this, "路由模式将在下次连接时生效。请断开后重新连接。",
                        "路由模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                RefreshState();
            };
            panel.Controls.Add(smartModeCheck);
            return panel;
        }

        private Button MakeButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(0, 34);
            button.Padding = new Padding(14, 0, 14, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = primary ? Brand : Color.FromArgb(241, 245, 249);
            button.ForeColor = primary ? Color.White : Ink;
            button.Margin = new Padding(0, 0, 8, 0);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private Control BuildContent()
        {
            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Canvas;
            content.Padding = new Padding(24, 16, 24, 4);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 128F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            content.Controls.Add(layout);

            Panel card = new Panel();
            card.Dock = DockStyle.Fill;
            card.BackColor = Color.White;
            card.Margin = new Padding(0);
            card.Paint += StatusCardPaint;
            card.Resize += delegate
            {
                ApplyCardRegion(card);
                PositionCardControls(card);
            };

            statusDot = new Panel();
            statusDot.Size = new Size(12, 12);
            statusDot.Location = new Point(22, 34);
            statusDot.BackColor = Color.White;
            statusDot.Paint += delegate(object sender, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush brush = new SolidBrush(statusDotColor))
                {
                    e.Graphics.FillEllipse(brush, 0, 0, statusDot.Width - 1, statusDot.Height - 1);
                }
            };
            card.Controls.Add(statusDot);

            statusTitle = new Label();
            statusTitle.AutoSize = true;
            statusTitle.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            statusTitle.ForeColor = Ink;
            statusTitle.Text = "未连接";
            statusTitle.Location = new Point(44, 22);
            card.Controls.Add(statusTitle);

            statusSubtitle = new Label();
            statusSubtitle.AutoSize = true;
            statusSubtitle.Font = new Font("Microsoft YaHei UI", 9F);
            statusSubtitle.ForeColor = Muted;
            statusSubtitle.Text = "导入配置后即可连接";
            statusSubtitle.Location = new Point(46, 58);
            card.Controls.Add(statusSubtitle);

            monthlyLabel = new Label();
            monthlyLabel.AutoSize = true;
            monthlyLabel.Font = new Font("Microsoft YaHei UI", 9F);
            monthlyLabel.ForeColor = Muted;
            monthlyLabel.Text = "本月流量：连接后获取";
            monthlyLabel.Location = new Point(46, 84);
            card.Controls.Add(monthlyLabel);

            connectButton = new Button();
            connectButton.Text = "连接";
            connectButton.Size = new Size(128, 40);
            connectButton.FlatStyle = FlatStyle.Flat;
            connectButton.FlatAppearance.BorderSize = 0;
            connectButton.BackColor = Brand;
            connectButton.ForeColor = Color.White;
            connectButton.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            connectButton.Cursor = Cursors.Hand;
            connectButton.UseVisualStyleBackColor = false;
            connectButton.Click += delegate { ToggleConnection(); };
            card.Controls.Add(connectButton);

            layout.Controls.Add(card, 0, 0);

            Panel listPanel = new Panel();
            listPanel.Dock = DockStyle.Fill;
            listPanel.BackColor = Canvas;
            listPanel.Margin = new Padding(0, 16, 0, 0);
            listPanel.Padding = new Padding(0);

            profileList = new ListView();
            profileList.Dock = DockStyle.Fill;
            profileList.View = View.Details;
            profileList.FullRowSelect = true;
            profileList.HideSelection = false;
            profileList.MultiSelect = false;
            profileList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            profileList.BorderStyle = BorderStyle.FixedSingle;
            profileList.BackColor = Color.White;
            profileList.ForeColor = Ink;
            profileList.Font = new Font("Microsoft YaHei UI", 9F);
            profileList.Columns.Add("节点名称", 190, HorizontalAlignment.Left);
            profileList.Columns.Add("服务器 IP", 180, HorizontalAlignment.Left);
            profileList.Columns.Add("位置 / 类型", 250, HorizontalAlignment.Left);
            profileList.Columns.Add("延迟", 90, HorizontalAlignment.Left);
            profileList.Columns.Add("状态", 100, HorizontalAlignment.Left);
            profileList.SelectedIndexChanged += delegate { RefreshState(); };
            profileList.DoubleClick += delegate { ToggleConnection(); };
            profileList.Resize += delegate { AdjustColumns(); };
            listPanel.Controls.Add(profileList);

            emptyLabel = new Label();
            emptyLabel.Dock = DockStyle.Fill;
            emptyLabel.BackColor = Color.White;
            emptyLabel.ForeColor = Muted;
            emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            emptyLabel.Font = new Font("Microsoft YaHei UI", 10F);
            emptyLabel.Text = "还没有节点配置\r\n\r\n点击上方「导入配置」或「粘贴配置」开始";
            emptyLabel.Visible = false;
            listPanel.Controls.Add(emptyLabel);

            layout.Controls.Add(listPanel, 0, 1);
            return content;
        }

        private Control BuildFooter()
        {
            Panel footer = new Panel();
            footer.Dock = DockStyle.Fill;
            footer.BackColor = Canvas;
            footer.Padding = new Padding(26, 10, 24, 8);

            footerLabel = new Label();
            footerLabel.Dock = DockStyle.Fill;
            footerLabel.TextAlign = ContentAlignment.MiddleLeft;
            footerLabel.ForeColor = Muted;
            footerLabel.Font = new Font("Microsoft YaHei UI", 8.5F);
            footerLabel.AutoEllipsis = true;
            footerLabel.Padding = new Padding(0, 0, 190, 0);
            footerLabel.Text = "正在检测 WireGuard……";
            footer.Controls.Add(footerLabel);

            installLink = new LinkLabel();
            installLink.AutoSize = true;
            installLink.Text = "下载安装 WireGuard →";
            installLink.Font = new Font("Microsoft YaHei UI", 8.5F);
            installLink.LinkColor = Brand;
            installLink.ActiveLinkColor = BrandDark;
            installLink.Visible = false;
            installLink.LinkClicked += delegate { PromptInstall(); };
            footer.Controls.Add(installLink);
            footer.Resize += delegate
            {
                installLink.Location = new Point(
                    footer.ClientSize.Width - installLink.Width - 24,
                    (footer.ClientSize.Height - installLink.Height) / 2);
            };
            return footer;
        }

        // ---------------------------------------------------------- 状态

        private void RefreshState()
        {
            if (busy)
                return;

            string running = FindActiveTunnelName();
            bool installed = WireGuardManager.IsInstalled;
            ProfileInfo selected = SelectedProfile();
            bool selectedRunning = selected != null && running != null
                && string.Equals(running, selected.TunnelName, StringComparison.OrdinalIgnoreCase);

            if (selectedRunning)
            {
                statusTitle.Text = "已连接";
                statusSubtitle.Text = selected.DisplayName + " · " + selected.Endpoint
                    + " · " + (runningSmartMode ? "智能分流" : "全局代理");
            }
            else if (running != null)
            {
                statusTitle.Text = "已连接";
                statusSubtitle.Text = "隧道 " + running + " 正在运行（不是当前选中的节点）";
            }
            else
            {
                statusTitle.Text = "未连接";
                if (selected != null)
                    statusSubtitle.Text = "已选择 " + selected.DisplayName
                        + " · " + (AppSettings.SmartMode ? "智能分流" : "全局代理")
                        + "，点击右侧按钮连接";
                else
                    statusSubtitle.Text = profiles.Count == 0 ? "导入配置后即可连接" : "选择一个节点开始";
            }

            statusDotColor = running != null ? Success : Muted;
            statusDot.Invalidate();

            if (running != null)
            {
                if (!string.Equals(monthlyStatsTunnel, running, StringComparison.OrdinalIgnoreCase))
                {
                    monthlyStatsTunnel = running;
                    monthlyLabel.Text = "本月流量：获取中……";
                    RefreshMonthlyStats();
                }
                if (!monthlyTimer.Enabled)
                    monthlyTimer.Start();
            }
            else
            {
                monthlyStatsTunnel = null;
                runningSmartMode = false;
                monthlyTimer.Stop();
                monthlyLabel.Text = "本月流量：连接后获取";
            }

            installLink.Visible = !installed;
            if (installed)
            {
                footerLabel.ForeColor = Muted;
                footerLabel.Text = "WireGuard 已安装 · 配置文件目录：" + ProfileStore.ProfilesDir;
            }
            else
            {
                footerLabel.ForeColor = Color.FromArgb(185, 28, 28);
                footerLabel.Text = "WireGuard 未安装 · 请先安装后再连接";
            }

            bool showDisconnect = running != null
                && (selected == null || selectedRunning);
            connectButton.Text = showDisconnect ? "断开连接" : "连接";
            connectButton.Enabled = !busy && installed && (selected != null || running != null);
            connectButton.BackColor = Brand;
            deleteButton.Enabled = !busy && selected != null;

            for (int i = 0; i < profileList.Items.Count; i++)
            {
                ListViewItem item = profileList.Items[i];
                ProfileInfo profile = item.Tag as ProfileInfo;
                bool isRunning = profile != null && running != null
                    && string.Equals(profile.TunnelName, running, StringComparison.OrdinalIgnoreCase);
                item.SubItems[4].Text = isRunning ? "运行中" : "已停止";
                item.SubItems[4].ForeColor = isRunning ? Color.FromArgb(22, 163, 74) : Muted;
            }
        }

        private ProfileInfo SelectedProfile()
        {
            if (profileList == null || profileList.SelectedItems.Count == 0)
                return null;
            return profileList.SelectedItems[0].Tag as ProfileInfo;
        }

        // ---------------------------------------------------------- 连接

        private void ToggleConnection()
        {
            if (busy)
                return;
            string running = FindActiveTunnelName();
            ProfileInfo selected = SelectedProfile();

            if (running != null && (selected == null
                || string.Equals(running, selected.TunnelName, StringComparison.OrdinalIgnoreCase)))
            {
                DisconnectAsync(running);
                return;
            }
            if (selected == null)
                return;
            if (!WireGuardManager.IsInstalled)
            {
                PromptInstall();
                return;
            }
            ConnectAsync(selected, running);
        }

        /// <summary>只查找由 LiteTunnel 配置创建的隧道，避免影响官方 WireGuard 客户端创建的其它隧道。</summary>
        private string FindActiveTunnelName()
        {
            List<string> running = WireGuardManager.GetRunningTunnelNames();
            foreach (ProfileInfo profile in profiles)
            {
                foreach (string name in running)
                {
                    if (string.Equals(profile.TunnelName, name, StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }
            return null;
        }

        /// <summary>从服务端 VPN 内网 API 刷新当前客户端的月度流量。</summary>
        private void RefreshMonthlyStats()
        {
            if (monthlyFetching)
                return;
            string running = FindActiveTunnelName();
            if (running == null)
                return;

            ProfileInfo profile = null;
            foreach (ProfileInfo item in profiles)
            {
                if (string.Equals(item.TunnelName, running, StringComparison.OrdinalIgnoreCase))
                {
                    profile = item;
                    break;
                }
            }
            if (profile == null)
                return;

            string configText;
            try
            {
                configText = File.ReadAllText(profile.FilePath, Encoding.UTF8);
            }
            catch (Exception)
            {
                monthlyLabel.Text = "本月流量：无法读取配置";
                return;
            }

            string url = ProfileStore.GetStatsUrl(configText);
            string token = ProfileStore.GetStatsToken(configText);
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
            {
                monthlyLabel.Text = "本月流量：服务端未配置统计";
                return;
            }

            monthlyFetching = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                TrafficResponse response = TrafficStatsClient.Fetch(url, token, out error);
                if (IsDisposed)
                    return;
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        monthlyFetching = false;
                        if (IsDisposed)
                            return;
                        if (!string.Equals(FindActiveTunnelName(), running,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                        if (response == null)
                        {
                            monthlyLabel.Text = "本月流量：暂时无法获取";
                        }
                        else
                        {
                            string period = string.IsNullOrEmpty(response.period)
                                ? string.Empty : "（" + response.period + "）";
                            // 服务端 rx = 服务器收到（客户端上传），tx = 服务器发出（客户端下载）
                            monthlyLabel.Text = "本月流量：↓ " + FormatBytes(response.tx)
                                + "   ↑ " + FormatBytes(response.rx) + period;
                        }
                    }));
                }
                catch (InvalidOperationException)
                {
                    monthlyFetching = false;
                }
            });
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return bytes.ToString() + " B";
            string[] units = new string[] { "KB", "MB", "GB", "TB" };
            double value = bytes;
            int index = -1;
            while (value >= 1024.0 && index < units.Length - 1)
            {
                value /= 1024.0;
                index++;
            }
            return value.ToString("0.0") + " " + units[index];
        }

        private void ConnectAsync(ProfileInfo profile, string activeTunnelName)
        {
            if (!WireGuardManager.IsInstalled && !EnsureWireGuardInstalled())
                return;

            runningSmartMode = AppSettings.SmartMode;
            RunAsync("正在连接……", delegate
            {
                if (activeTunnelName != null)
                    WireGuardManager.StopTunnel(activeTunnelName);
                string configPath = profile.FilePath;
                if (AppSettings.SmartMode)
                {
                    string original = File.ReadAllText(profile.FilePath, Encoding.UTF8);
                    configPath = SmartRoute.BuildSmartConfig(profile, original);
                }
                WireGuardManager.StartTunnel(configPath, profile.TunnelName);
            });
        }

        private bool EnsureWireGuardInstalled()
        {
            DialogResult answer = MessageBox.Show(this,
                "未检测到 WireGuard for Windows。\r\n\r\n" +
                "轻隧需要调用官方 WireGuard 的隧道服务。是否现在自动下载并安装？\r\n" +
                "会优先从 GitHub 下载官方 MSI（不依赖 wireguard.com），失败后再尝试 winget。\r\n" +
                "安装过程需要管理员权限。",
                "需要安装 WireGuard",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
                return false;

            string message;
            bool installed;
            Cursor previous = Cursor;
            Cursor = Cursors.WaitCursor;
            try
            {
                installed = WireGuardManager.InstallWireGuard(out message);
            }
            finally
            {
                Cursor = previous;
            }

            if (!installed)
            {
                MessageBox.Show(this,
                    "自动安装 WireGuard 失败。\r\n\r\n" + message,
                    "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                if (MessageBox.Show(this, "是否打开 Microsoft Store 搜索 WireGuard？",
                        "手动安装", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    try
                    {
                        Process.Start("ms-windows-store://search?query=WireGuard");
                    }
                    catch (Exception)
                    {
                    }
                }
                return false;
            }

            MessageBox.Show(this, "WireGuard 已安装，继续连接。",
                "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        private void DisconnectAsync(string tunnelName)
        {
            RunAsync("正在断开……", delegate
            {
                WireGuardManager.StopTunnel(tunnelName);
            });
        }

        private void RunAsync(string message, Action work)
        {
            if (busy)
                return;
            SetBusy(true, message);
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                Exception error = null;
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                if (IsDisposed)
                    return;
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (IsDisposed)
                            return;
                        SetBusy(false, null);
                        if (error != null)
                        {
                            MessageBox.Show(this, error.Message, "操作失败",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        RefreshState();
                    }));
                }
                catch (InvalidOperationException)
                {
                }
            });
        }

        private void SetBusy(bool value, string message)
        {
            busy = value;
            Cursor = value ? Cursors.WaitCursor : Cursors.Default;
            importButton.Enabled = !value;
            pasteButton.Enabled = !value;
            openFolderButton.Enabled = !value;
            aboutButton.Enabled = !value;
            profileList.Enabled = !value;
            deleteButton.Enabled = !value && SelectedProfile() != null;
            if (value)
            {
                connectButton.Enabled = false;
                statusTitle.Text = message != null ? message : "请稍候……";
                statusSubtitle.Text = "正在调用 WireGuard 服务，请稍候";
            }
        }

        // ---------------------------------------------------------- 配置管理

        private void LoadProfiles(string selectTunnelName)
        {
            profiles = ProfileStore.Load();
            profileList.BeginUpdate();
            profileList.Items.Clear();
            foreach (ProfileInfo profile in profiles)
            {
                string configText = string.Empty;
                try
                {
                    configText = File.ReadAllText(profile.FilePath, Encoding.UTF8);
                }
                catch (Exception)
                {
                }
                string host = ProfileStore.GetServerHost(configText);
                if (string.IsNullOrEmpty(host))
                    host = profile.Endpoint;
                string location = ProfileStore.GetLocation(configText);
                string serverType = ProfileStore.GetServerType(configText);

                ListViewItem item = new ListViewItem(profile.DisplayName);
                item.UseItemStyleForSubItems = false;
                item.SubItems.Add(host);
                item.SubItems.Add(location + " · " + serverType);
                item.SubItems.Add("— ms");
                item.SubItems.Add("已停止");
                item.Tag = profile;
                profileList.Items.Add(item);
            }
            profileList.EndUpdate();
            AdjustColumns();

            profileList.Visible = profiles.Count > 0;
            emptyLabel.Visible = profiles.Count == 0;

            if (selectTunnelName != null)
                SelectTunnel(selectTunnelName);
            if (profileList.SelectedItems.Count == 0 && profileList.Items.Count > 0)
                profileList.Items[0].Selected = true;

            RefreshState();
            MeasureLatencies();
        }

        private void SelectTunnel(string tunnelName)
        {
            for (int i = 0; i < profileList.Items.Count; i++)
            {
                ProfileInfo profile = profileList.Items[i].Tag as ProfileInfo;
                if (profile != null && string.Equals(profile.TunnelName, tunnelName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    profileList.Items[i].Selected = true;
                    profileList.Items[i].Focused = true;
                    profileList.EnsureVisible(i);
                    break;
                }
            }
        }

        private void ImportProfiles()
        {
            if (busy)
                return;
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择 WireGuard 配置文件";
                dialog.Filter = "WireGuard 配置 (*.conf)|*.conf|所有文件 (*.*)|*.*";
                dialog.Multiselect = true;
                dialog.RestoreDirectory = true;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                List<string> errors = new List<string>();
                string lastTunnel = null;
                foreach (string file in dialog.FileNames)
                {
                    string error;
                    ProfileInfo info = ProfileStore.Import(file, out error);
                    if (info == null)
                        errors.Add(Path.GetFileName(file) + "：" + error);
                    else
                        lastTunnel = info.TunnelName;
                }
                LoadProfiles(lastTunnel);
                if (errors.Count > 0)
                {
                    MessageBox.Show(this, string.Join("\r\n", errors.ToArray()),
                        "部分文件未能导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void PasteProfile()
        {
            if (busy)
                return;
            using (Form dialog = new Form())
            {
                dialog.Text = "粘贴 WireGuard 配置";
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.ClientSize = new Size(680, 520);
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.Font = Font;
                dialog.BackColor = Color.White;

                Label nameLabel = new Label();
                nameLabel.Text = "节点名称";
                nameLabel.AutoSize = true;
                nameLabel.Location = new Point(20, 18);
                dialog.Controls.Add(nameLabel);

                TextBox nameBox = new TextBox();
                nameBox.Location = new Point(20, 42);
                nameBox.Width = 640;
                nameBox.Text = "我的节点";
                dialog.Controls.Add(nameBox);

                Label configLabel = new Label();
                configLabel.Text = "WireGuard 配置内容（可粘贴完整的 .conf 内容）";
                configLabel.AutoSize = true;
                configLabel.Location = new Point(20, 78);
                dialog.Controls.Add(configLabel);

                TextBox configBox = new TextBox();
                configBox.Multiline = true;
                configBox.ScrollBars = ScrollBars.Both;
                configBox.WordWrap = false;
                configBox.AcceptsReturn = true;
                configBox.AcceptsTab = true;
                configBox.Font = new Font("Consolas", 9F);
                configBox.Location = new Point(20, 102);
                configBox.Size = new Size(640, 330);
                configBox.Anchor = AnchorStyles.Top | AnchorStyles.Left
                    | AnchorStyles.Right | AnchorStyles.Bottom;
                dialog.Controls.Add(configBox);

                Button clipboardButton = new Button();
                clipboardButton.Text = "从剪贴板填入";
                clipboardButton.Location = new Point(20, 444);
                clipboardButton.Size = new Size(140, 32);
                clipboardButton.FlatStyle = FlatStyle.Flat;
                clipboardButton.FlatAppearance.BorderColor = Line;
                clipboardButton.Click += delegate
                {
                    try
                    {
                        if (Clipboard.ContainsText())
                            configBox.Text = Clipboard.GetText();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dialog, "读取剪贴板失败：" + ex.Message,
                            "LiteTunnel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                dialog.Controls.Add(clipboardButton);

                Button okButton = new Button();
                okButton.Text = "保存";
                okButton.Location = new Point(460, 444);
                okButton.Size = new Size(96, 32);
                okButton.BackColor = Brand;
                okButton.ForeColor = Color.White;
                okButton.FlatStyle = FlatStyle.Flat;
                okButton.FlatAppearance.BorderSize = 0;
                okButton.UseVisualStyleBackColor = false;
                dialog.Controls.Add(okButton);

                Button cancelButton = new Button();
                cancelButton.Text = "取消";
                cancelButton.Location = new Point(564, 444);
                cancelButton.Size = new Size(96, 32);
                cancelButton.DialogResult = DialogResult.Cancel;
                dialog.Controls.Add(cancelButton);
                dialog.CancelButton = cancelButton;

                string createdTunnel = null;
                okButton.Click += delegate
                {
                    string error;
                    if (!ProfileStore.ValidateConfig(configBox.Text, out error))
                    {
                        MessageBox.Show(dialog, error, "配置格式不正确",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    ProfileInfo info = ProfileStore.Create(nameBox.Text, configBox.Text, out error);
                    if (info == null)
                    {
                        MessageBox.Show(dialog, error, "无法保存",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    createdTunnel = info.TunnelName;
                    dialog.DialogResult = DialogResult.OK;
                };

                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string clipboardText = Clipboard.GetText();
                        if (clipboardText.IndexOf("[Interface]", StringComparison.OrdinalIgnoreCase) >= 0)
                            configBox.Text = clipboardText;
                    }
                }
                catch (Exception)
                {
                }
                string suggested = ProfileStore.GetDisplayName(configBox.Text);
                if (!string.IsNullOrEmpty(suggested))
                    nameBox.Text = suggested;

                if (dialog.ShowDialog(this) == DialogResult.OK && createdTunnel != null)
                    LoadProfiles(createdTunnel);
            }
        }

        private void DeleteProfile()
        {
            ProfileInfo selected = SelectedProfile();
            if (selected == null)
                return;
            string running = FindActiveTunnelName();
            if (running != null && string.Equals(running, selected.TunnelName,
                StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "该节点正在连接中，请先断开连接再删除。",
                    "无法删除", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult result = MessageBox.Show(this,
                "确定删除节点「" + selected.DisplayName + "」吗？\r\n\r\n对应的 .conf 文件会被删除。",
                "删除节点", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes)
                return;
            try
            {
                File.Delete(selected.FilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "删除失败：" + ex.Message, "删除节点",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            LoadProfiles(null);
        }

        private void OpenProfileFolder()
        {
            try
            {
                ProfileStore.EnsureDirectory();
                ProcessStartInfo startInfo = new ProcessStartInfo("explorer.exe",
                    "\"" + ProfileStore.ProfilesDir + "\"");
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开配置目录：" + ex.Message, "LiteTunnel",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PromptInstall()
        {
            DialogResult result = MessageBox.Show(this,
                "连接前需要先安装 WireGuard for Windows（免费、开源）。\r\n\r\n" +
                "点击“是”打开官方下载页面，安装完成后回到本程序重试。",
                "需要安装 WireGuard", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (result == DialogResult.Yes)
            {
                try
                {
                    Process.Start("https://www.wireguard.com/install/");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "无法打开浏览器：" + ex.Message, "LiteTunnel",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ShowAbout()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            string versionText = version != null ? version.ToString(3) : "1.1.0";
            MessageBox.Show(this,
                "轻隧 LiteTunnel v" + versionText + "\r\n\r\n" +
                "一个基于 WireGuard 的 Windows VPN 客户端。\r\n" +
                "隧道由官方开源的 WireGuard for Windows 提供。\r\n\r\n" +
                "配置文件目录：\r\n" + ProfileStore.ProfilesDir + "\r\n\r\n" +
                "本项目以 Apache-2.0 协议开源。",
                "关于轻隧", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------------------------------------------------- 绘制

        private void StatusCardPaint(object sender, PaintEventArgs e)
        {
            Panel card = (Panel)sender;
            if (card.Width < 4 || card.Height < 4)
                return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(1, 1, card.Width - 3, card.Height - 3);
            using (GraphicsPath path = RoundedRect(rect, 14))
            using (Pen pen = new Pen(Line, 1F))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        private static void ApplyCardRegion(Panel card)
        {
            if (card.Width <= 0 || card.Height <= 0)
                return;
            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, card.Width, card.Height), 14))
            {
                Region old = card.Region;
                card.Region = new Region(path);
                if (old != null)
                    old.Dispose();
            }
        }

        private void PositionCardControls(Panel card)
        {
            connectButton.Location = new Point(
                card.ClientSize.Width - connectButton.Width - 22,
                (card.ClientSize.Height - connectButton.Height) / 2);
        }

        private void MeasureLatencies()
        {
            foreach (ProfileInfo profile in profiles)
            {
                string text;
                try
                {
                    text = File.ReadAllText(profile.FilePath, Encoding.UTF8);
                }
                catch (Exception)
                {
                    SetLatencyItem(profile, "— ms");
                    continue;
                }

                string host = ProfileStore.GetServerHost(text);
                int port = ProfileStore.GetProbePort(text);
                if (string.IsNullOrEmpty(host) || port == 0)
                {
                    SetLatencyItem(profile, "— ms");
                    continue;
                }

                string tunnelName = profile.TunnelName;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    long? latency = TcpLatency(host, port);
                    if (IsDisposed)
                        return;
                    try
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            if (IsDisposed)
                                return;
                            foreach (ListViewItem item in profileList.Items)
                            {
                                ProfileInfo itemProfile = item.Tag as ProfileInfo;
                                if (itemProfile != null && string.Equals(itemProfile.TunnelName,
                                    tunnelName, StringComparison.OrdinalIgnoreCase))
                                {
                                    item.SubItems[3].Text = latency.HasValue
                                        ? latency.Value + " ms" : "— ms";
                                    break;
                                }
                            }
                        }));
                    }
                    catch (InvalidOperationException)
                    {
                    }
                });
            }
        }

        private void SetLatencyItem(ProfileInfo profile, string text)
        {
            if (profileList == null)
                return;
            foreach (ListViewItem item in profileList.Items)
            {
                ProfileInfo itemProfile = item.Tag as ProfileInfo;
                if (itemProfile != null && string.Equals(itemProfile.TunnelName,
                    profile.TunnelName, StringComparison.OrdinalIgnoreCase))
                {
                    item.SubItems[3].Text = text;
                    break;
                }
            }
        }

        private static long? TcpLatency(string host, int port)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult result = client.BeginConnect(host, port, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(1500))
                        return null;
                    client.EndConnect(result);
                    return watch.ElapsedMilliseconds;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void AdjustColumns()
        {
            if (profileList == null || profileList.Columns.Count < 5)
                return;
            int width = profileList.ClientSize.Width - 4;
            if (width < 500)
                return;
            int first = (int)(width * 0.22);
            int second = (int)(width * 0.22);
            int third = (int)(width * 0.28);
            int fourth = (int)(width * 0.12);
            profileList.Columns[0].Width = first;
            profileList.Columns[1].Width = second;
            profileList.Columns[2].Width = third;
            profileList.Columns[3].Width = fourth;
            profileList.Columns[4].Width = Math.Max(70, width - first - second - third - fourth);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            int diameter = radius * 2;
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (refreshTimer != null)
            {
                refreshTimer.Stop();
                refreshTimer.Dispose();
            }
            if (monthlyTimer != null)
            {
                monthlyTimer.Stop();
                monthlyTimer.Dispose();
            }
            if (latencyTimer != null)
            {
                latencyTimer.Stop();
                latencyTimer.Dispose();
            }
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            if (trayMenu != null)
            {
                trayMenu.Dispose();
                trayMenu = null;
            }
            base.OnFormClosed(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                e.Cancel = true;
                return;
            }
            if (!allowExit && e.CloseReason != CloseReason.WindowsShutDown)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
