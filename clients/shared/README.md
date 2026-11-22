# 智能分流路由列表

`non_china_ipv4.txt` 用于客户端“智能分流”：

- 中国 IPv4 段、私网、保留地址 → 客户端直连；
- 其它公网 IPv4 段 → 进入 WireGuard。

数据来源：APNIC `delegated-apnic-latest`，生成时间为 2026-11。
列表是 **IP 段** 分流，不是域名分流，因此会有少量 CDN 误判：
例如使用国内 CDN 的国外站点可能直连，使用国外 CDN 的国内站点可能走 VPN。

重新生成：

```bash
curl -o delegated-apnic-latest https://ftp.apnic.net/apnic/stats/apnic/delegated-apnic-latest
python3 generate_routes.py delegated-apnic-latest
```

然后把 `non_china_ipv4.txt` 复制到：

```text
clients/android/app/src/main/assets/non_china_ipv4.txt
clients/windows/assets/non_china_ipv4.txt
```

重新构建客户端即可。APNIC 数据仅用于网络路由分类。
