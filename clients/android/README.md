# 轻隧 Android 客户端

Kotlin + Material 3 编写的 WireGuard 客户端，隧道能力来自官方
[WireGuard Android tunnel library](https://github.com/WireGuard/wireguard-android)。

## 环境要求

- JDK 17+
- Android SDK Platform 35、Build Tools 35
- 最低运行版本 Android 8.0（API 26）

## 构建

```bash
./gradlew assembleDebug
# 或
./gradlew assembleRelease
```

产物位于：

```text
app/build/outputs/apk/debug/app-debug.apk
app/build/outputs/apk/release/app-release.apk
```

`release` 默认使用调试签名，方便直接安装。正式发布前请生成自己的 keystore 并修改
`app/build.gradle.kts` 中的 `signingConfigs`。

> 如果 Gradle 下载太慢，可在 `gradle/wrapper/gradle-wrapper.properties` 中把
> `distributionUrl` 换成国内镜像。

## 功能

- 节点列表（名称、服务器 IP、位置/类型、连接状态、TCP 延迟）
- 可保存多个不同服务器的配置，点击列表切换连接
- 服务端月度流量：从 VPN 内网 API 读取，与 PC 客户端显示同一份数据
- 扫码导入二维码
- 粘贴完整 WireGuard 配置
- 从文件导入 `.conf`
- 长按节点：复制配置 / 编辑 / 删除
- 连接状态、连接时长、实时接收/发送流量
- 配置保存在应用私有 SharedPreferences，关闭系统备份

## 代码结构

```text
app/src/main/java/com/litetunnel/app/
├── MainActivity.kt       # 界面、扫码、导入、连接控制
├── VpnController.kt      # GoBackend 封装与全局隧道状态
├── ProfileStore.kt       # 配置持久化
├── ProfileAdapter.kt     # 节点列表
├── Profile.kt
└── Utils.kt              # 格式化、隧道名清洗
```

## 权限说明

- `INTERNET` / `ACCESS_NETWORK_STATE`：基础网络能力
- `CAMERA`：扫描二维码
- VPN 授权：由系统在首次连接时弹窗确认；应用不申请 root

## 注意

- 同一时刻只保留一条隧道；切换节点会断开旧节点。
- 应用没有实现开机自连、分应用代理、Always-on 等高级功能，可按需扩展。
- 按 Home/返回通常不会断开 VPN；被系统强制停止或回收进程时会断开。
- 月度流量依赖服务端生成的 `# StatsURL` 和 `# StatsToken` 注释；旧版配置需要重新添加客户端。
- 月度流量 API 使用 HTTP，但只应该通过 WireGuard 隧道访问 VPN 内网地址。
