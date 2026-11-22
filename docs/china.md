# 在中国大陆网络环境下使用

## 先说结论

- **WireGuard 是 UDP 协议**，流量特征明显。在部分运营商/地区会被 QoS 限速、丢包甚至阻断。
- 本项目自带客户端只实现了 WireGuard。**如果你在中国大陆，建议先小流量测试**：有时可用，有时不稳定。
- 如果 WireGuard 不通，抗封锁通常要换 **TCP + TLS 伪装** 方案，例如 **Xray（VLESS + REALITY）** 或 Trojan。这类方案可以直接使用成熟的开源客户端，无需自己写客户端。

## 先做三个小测试

1. 把端口改成 443/UDP 重新安装：

   ```bash
   sudo litetunnel uninstall
   sudo WG_PORT=443 WG_ENDPOINT=你的公网IP bash server/install.sh
   ```

2. 分别用手机流量、家庭宽带、公司网络测试，不同运营商差异很大。
3. 在服务器上看是否有握手：

   ```bash
   sudo wg show
   sudo litetunnel list
   ```

   如果客户端显示已发送但服务器完全没有握手，基本可以判断 UDP 被拦截。

## 推荐替代：Xray VLESS + REALITY

REALITY 使用真实网站的 TLS 握手作为伪装，服务端不需要自己的域名和证书，客户端使用 TCP 443，抗封锁能力比裸 WireGuard 强很多。

> 下面是最小化手动部署示例，适合学习。生产环境请阅读 [Xray 官方文档](https://xtls.github.io/) 并保持版本更新。

### 1. 安装 Xray

在 Debian/Ubuntu 服务器上：

```bash
bash -c "$(curl -L https://github.com/XTLS/Xray-install/raw/main/install-release.sh)" @ install
```

如果 GitHub 访问困难，可以先在本地下载官方 release 压缩包，再上传到服务器解压安装。

### 2. 生成参数

```bash
xray uuid                 # 生成客户端 UUID
xray x25519               # 生成 REALITY 私钥/公钥，保存两行输出
openssl rand -hex 8       # 生成 shortId
```

### 3. 写入配置

编辑 `/usr/local/etc/xray/config.json`（把 `UUID`、`PRIVATE_KEY`、`SHORT_ID` 换成上一步的值）：

```json
{
  "log": { "loglevel": "warning" },
  "inbounds": [
    {
      "listen": "0.0.0.0",
      "port": 443,
      "protocol": "vless",
      "settings": {
        "clients": [
          { "id": "UUID", "flow": "xtls-rprx-vision" }
        ],
        "decryption": "none"
      },
      "streamSettings": {
        "network": "tcp",
        "security": "reality",
        "realitySettings": {
          "show": false,
          "dest": "www.microsoft.com:443",
          "xver": 0,
          "serverNames": ["www.microsoft.com"],
          "privateKey": "PRIVATE_KEY",
          "shortIds": ["SHORT_ID"]
        }
      },
      "sniffing": {
        "enabled": true,
        "destOverride": ["http", "tls", "quic"]
      }
    }
  ],
  "outbounds": [
    { "protocol": "freedom", "tag": "direct" }
  ]
}
```

检查并启动：

```bash
xray -test -c /usr/local/etc/xray/config.json
systemctl restart xray
systemctl enable xray
```

安全组/防火墙放行 **TCP 443**。如果服务器上已经有网站占用 443，需要先停掉或改成别的端口。

### 4. 客户端导入链接

把下面链接中的大写占位符替换成实际值：

```text
vless://UUID@服务器IP:443?encryption=none&flow=xtls-rprx-vision&security=reality&sni=www.microsoft.com&fp=chrome&pbk=PUBLIC_KEY&sid=SHORT_ID&type=tcp#LiteTunnel-Reality
```

- Android：[v2rayNG](https://github.com/2dust/v2rayNG/releases)（开源）→ 从剪贴板导入；
- Windows：[v2rayN](https://github.com/2dust/v2rayN/releases)（开源）→ 从剪贴板导入；
- iOS：App Store 中支持 VLESS/REALITY 的客户端（例如 Shadowrocket，付费）。

也可以在服务器上用 `qrencode` 把链接生成二维码给手机扫：

```bash
qrencode -t ANSIUTF8 'vless://...'
```

### 5. 注意事项

- `dest` / `serverNames` 推荐使用境外、支持 TLS 1.3 的大站（如 `www.microsoft.com`），并确保服务器能访问它；
- 不要把自己的域名或个人信息暴露在链接里，REALITY 本身不需要域名；
- 定期升级 Xray，关注官方安全公告；
- 如果 443 被重点照顾，可以尝试其它 TCP 端口，但 443 通常最不显眼；
- Hysteria2（QUIC/UDP）速度快但同样可能被 UDP 干扰，可作为备选。

## 合规提醒

不同国家和地区对 VPN、跨境网络访问有不同法律规定。请仅将本项目和上述工具用于合法的学习、远程办公、访问自有服务器等用途，并遵守当地法律及云服务商条款。
