#!/usr/bin/env python3
"""从 APNIC delegated-apnic-latest 生成中国 / 非中国 IPv4 CIDR 列表。

用法：
  curl -o delegated-apnic-latest https://ftp.apnic.net/apnic/stats/apnic/delegated-apnic-latest
  python3 generate_routes.py delegated-apnic-latest

输出：
  china_ipv4.txt       中国 IPv4 段
  non_china_ipv4.txt   需要走 VPN 的 IPv4 段（已排除中国、私网和保留地址）

然后复制 non_china_ipv4.txt 到：
  clients/android/app/src/main/assets/
  clients/windows/assets/
"""

import ipaddress
import math
import os
import sys


DIRECT_RANGES = [
    "0.0.0.0/8",
    "10.0.0.0/8",
    "100.64.0.0/10",
    "127.0.0.0/8",
    "169.254.0.0/16",
    "172.16.0.0/12",
    "192.0.0.0/24",
    "192.0.2.0/24",
    "192.88.99.0/24",
    "192.168.0.0/16",
    "198.18.0.0/15",
    "198.51.100.0/24",
    "203.0.113.0/24",
    "224.0.0.0/4",
    "240.0.0.0/4",
]


def parse_china(path):
    networks = []
    with open(path, "r", encoding="utf-8", errors="ignore") as handle:
        for line in handle:
            parts = line.strip().split("|")
            if len(parts) < 5 or parts[1] != "CN" or parts[2] != "ipv4":
                continue
            try:
                count = int(parts[4])
                address = ipaddress.IPv4Address(parts[3])
            except Exception:
                continue
            if count <= 0 or count & (count - 1):
                continue
            prefix = 32 - int(math.log2(count))
            networks.append(ipaddress.ip_network((address, prefix), strict=False))
    return list(ipaddress.collapse_addresses(networks))


def complement(direct_networks):
    ranges = sorted(
        (int(net.network_address), int(net.broadcast_address))
        for net in direct_networks
    )
    merged = []
    for start, end in ranges:
        if merged and start <= merged[-1][1] + 1:
            merged[-1] = (merged[-1][0], max(merged[-1][1], end))
        else:
            merged.append((start, end))

    result = []
    cursor = 0
    for start, end in merged:
        if cursor < start:
            result.extend(
                ipaddress.summarize_address_range(
                    ipaddress.IPv4Address(cursor), ipaddress.IPv4Address(start - 1)
                )
            )
        cursor = max(cursor, end + 1)
    if cursor <= 0xFFFFFFFF:
        result.extend(
            ipaddress.summarize_address_range(
                ipaddress.IPv4Address(cursor), ipaddress.IPv4Address(0xFFFFFFFF)
            )
        )
    return list(ipaddress.collapse_addresses(result))


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 1
    source = sys.argv[1]
    china = parse_china(source)
    direct = list(ipaddress.collapse_addresses(
        china + [ipaddress.ip_network(item) for item in DIRECT_RANGES]
    ))
    vpn = complement(direct)

    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, "china_ipv4.txt"), "w", encoding="utf-8", newline="\n") as handle:
        for net in china:
            handle.write(str(net) + "\n")
    with open(os.path.join(here, "non_china_ipv4.txt"), "w", encoding="utf-8", newline="\n") as handle:
        for net in vpn:
            handle.write(str(net) + "\n")

    print("china:", len(china), "non-china:", len(vpn))
    print("copy non_china_ipv4.txt to Android assets/ and Windows assets/")
    return 0


if __name__ == "__main__":
    sys.exit(main())
