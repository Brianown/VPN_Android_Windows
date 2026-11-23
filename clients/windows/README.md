# 轻隧 Windows 客户端

基于 .NET Framework 4.8 的 WinForms 单文件客户端。隧道由官方开源的
[WireGuard for Windows](https://www.wireguard.com/install/) 提供，本程序负责导入配置、
创建/删除隧道服务、显示状态，并从服务端 VPN 内网 API 读取本月流量。

## 运行要求

- Windows 10 / 11（自带 .NET Framework 4.8）
- 首次连接时如果未安装 WireGuard for Windows，会优先从 GitHub 下载官方 MSI 静默安装；失败后再尝试 winget
- 管理员权限（UAC 提示）

## 构建

无需安装 Visual Studio 或 .NET SDK，使用系统自带的 `csc.exe`：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

产物：`dist\LiteTunnel.exe`。

也可以在 Visual Studio 2022 中打开 `LiteTunnel.csproj` 构建。

## 使用

1. 安装 WireGuard for Windows；
2. 以管理员身份运行 `LiteTunnel.exe`；
3. **导入配置** 选择服务端生成的 `.conf`，或 **粘贴配置**；
4. 选中节点 → **连接**；
5. 连接成功后状态卡会显示“本月流量：↓ … ↑ …（YYYY-MM）”，每 60 秒刷新一次；
6. 列表会显示每个节点的服务器 IP、位置/类型、TCP 延迟；点击其它节点即可切换服务器；
7. 工具栏勾选“智能分流（国内直连）”后，国外 IP 走 VPN、国内 IP 直连；切换后重新连接生效。
5. 再次点击 **断开连接**。

配置保存在：

```text
%APPDATA%\LiteTunnel\profiles\*.conf
```

文件名就是 WireGuard 隧道名（ASCII），显示名称保存在配置顶部的 `# Name = ...` 注释里。

> 点击窗口右上角关闭按钮不会退出，而是隐藏到托盘，VPN 保持连接。
> 只有点击工具栏的 **退出**，或右键托盘图标 → “退出 LiteTunnel”，才会真正结束进程（并断开本程序创建的隧道）。
> 程序只识别并管理该目录下的 LiteTunnel 隧道，不会动官方 WireGuard 客户端创建的其它隧道。

月度流量依赖服务端 v1.1.0+ 生成的 `# StatsURL` 和 `# StatsToken` 注释；
旧版配置重新导入或重新执行 `sudo litetunnel add` 后即可显示。

## 代码结构

```text
src/
├── Program.cs            # 入口
├── MainForm.cs           # 全部界面与交互
├── ProfileStore.cs       # 配置读写、校验、命名
├── WireGuardManager.cs   # 调用 wireguard.exe 与服务状态检测
└── AssemblyInfo.cs
app.manifest              # requireAdministrator + PerMonitorV2 DPI
build.ps1                 # csc 编译脚本
```

## 常见问题

- **提示未检测到 WireGuard**：先安装官方客户端，安装后重启本程序。
- **连接失败**：查看 Windows 事件查看器 → 应用程序日志，以及
  `C:\Program Files\WireGuard\wireguard.exe` 是否能正常手动导入配置。
- **关闭程序后 VPN 还在**：这是预期行为，隧道由 Windows 服务运行；重新打开程序断开即可。
- **想恢复官方客户端管理**：直接删除 `%APPDATA%\LiteTunnel\profiles` 下对应 `.conf`，
  或用官方 WireGuard 客户端导入同一份配置。

## v1.1.1

- 修复智能分流模式下 DNS 1.1.1.1/8.8.8.8 未进入隧道导致 YouTube 打不开的问题。
- 国内 IP 继续直连，国外 IP 走代理。
