# 内置 wireguard-go

Linux 内核没有 WireGuard 模块时（典型：CentOS 7 的 3.10 内核），安装脚本会把这里的
静态 `wireguard-go` 安装到 `/usr/local/bin/wireguard-go`，`wg-quick` 会自动回退到用户态实现。

- 源码版本：`wireguard-go 0.0.20230223`
- 构建方式：`CGO_ENABLED=0 GOOS=linux GOARCH=amd64/arm64 go build -trimpath -ldflags "-s -w"`
- 上游：<https://github.com/WireGuard/wireguard-go>
- 许可证：MIT（见项目根目录 `NOTICE`）
- 二进制为静态 ELF，不依赖目标机的 glibc，适用于 CentOS 7 等旧系统。

| 文件 | SHA-256 |
| --- | --- |
| `wireguard-go-linux-amd64` | `b42e317f0483e419d6796e73b0e2a5cef7520c3916dcd06493deedabbbbbf4cc` |
| `wireguard-go-linux-arm64` | `e3bbfecedb2f203c8f626d21b46f8043329cf1051e3f238ebd59f146d00e8a4a` |
