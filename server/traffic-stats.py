#!/usr/bin/env python3
"""LiteTunnel 月度流量统计采集器。

每次运行读取 `wg show <iface> dump` 的累计计数器，计算与上一次采样的差值，
累加到当前自然月的总流量，并原子写入 state JSON。

- 服务重启、WireGuard 重载导致计数器归零时自动按“当前值”处理；
- 跨月后第一次运行时清零并重新累计（在每月 1 日 00:00 后生效）；
- 时区默认 Asia/Shanghai（UTC+8），可用 --timezone 修改。

只依赖 Python 3 标准库。
"""

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timedelta, timezone

try:
    from zoneinfo import ZoneInfo
except ImportError:  # Python < 3.9
    ZoneInfo = None


NAME_RE = re.compile(r"^#\s*litetunnel:\s*name=(\S+)\s*$")
TOKEN_RE = re.compile(r"^#\s*litetunnel:\s*token=(\S+)\s*$")
PUBKEY_RE = re.compile(r"^PublicKey\s*=\s*(\S+)\s*$")


def parse_args():
    parser = argparse.ArgumentParser(description="LiteTunnel monthly traffic collector")
    parser.add_argument("--interface", default="wg0")
    parser.add_argument("--config", default="/etc/wireguard/wg0.conf")
    parser.add_argument("--state", default="/var/lib/litetunnel/traffic.json")
    parser.add_argument("--wg-binary", default="wg")
    parser.add_argument("--timezone", default="Asia/Shanghai")
    parser.add_argument(
        "--wg-dump-file",
        default=None,
        help="调试用：从文件读取 wg dump 输出，而不是执行 wg 命令",
    )
    return parser.parse_args()


def get_timezone(name):
    if not name:
        return None
    if name in ("Asia/Shanghai", "PRC", "Asia/Chongqing", "Asia/Harbin"):
        # 中国没有夏令时，固定 UTC+8 可兼容没有 tzdata 的旧 Python。
        return timezone(timedelta(hours=8), name="Asia/Shanghai")
    if ZoneInfo is not None:
        try:
            return ZoneInfo(name)
        except Exception:
            return None
    return None


def load_peers(config_path):
    """解析配置中的 [Peer]：name / token / public key。"""
    peers = {}
    current = None
    in_peer = False

    def flush():
        nonlocal current
        if current and current.get("name") and current.get("public_key"):
            peers[current["name"]] = {
                "name": current["name"],
                "token": current.get("token", ""),
                "public_key": current["public_key"],
            }
        current = None

    try:
        with open(config_path, "r", encoding="utf-8") as handle:
            for raw in handle:
                line = raw.strip()
                if not line:
                    continue
                if line.startswith("[") and line.endswith("]"):
                    if in_peer:
                        flush()
                    in_peer = line.lower() == "[peer]"
                    if in_peer:
                        current = {}
                    continue
                if not in_peer or current is None:
                    continue
                match = NAME_RE.match(line)
                if match:
                    current["name"] = match.group(1)
                    continue
                match = TOKEN_RE.match(line)
                if match:
                    current["token"] = match.group(1)
                    continue
                match = PUBKEY_RE.match(line)
                if match:
                    current["public_key"] = match.group(1)
    except FileNotFoundError:
        raise SystemExit("配置文件不存在：%s" % config_path)

    if in_peer:
        flush()
    return peers


def read_wg_dump(args):
    if args.wg_dump_file:
        with open(args.wg_dump_file, "r", encoding="utf-8") as handle:
            return handle.read()

    try:
        output = subprocess.check_output(
            [args.wg_binary, "show", args.interface, "dump"],
            text=True,
            stderr=subprocess.STDOUT,
            timeout=15,
        )
    except FileNotFoundError:
        raise SystemExit("找不到 wg 命令：%s" % args.wg_binary)
    except subprocess.CalledProcessError as exc:
        raise SystemExit("wg show 执行失败：%s" % (exc.output or "").strip())
    except subprocess.TimeoutExpired:
        raise SystemExit("wg show 执行超时")
    return output


def parse_wg_dump(text):
    """返回 {public_key: {"rx": int, "tx": int}}。"""
    peers = {}
    for index, line in enumerate(text.splitlines()):
        if index == 0:
            continue  # 第一行是 interface 信息
        parts = line.split("\t")
        if len(parts) < 8:
            continue
        public_key = parts[0].strip()
        if not public_key:
            continue
        try:
            rx = int(parts[5] or 0)
            tx = int(parts[6] or 0)
        except ValueError:
            continue
        peers[public_key] = {"rx": rx, "tx": tx}
    return peers


def load_state(path):
    try:
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
        if isinstance(data, dict) and isinstance(data.get("peers"), dict):
            return data
    except (FileNotFoundError, ValueError, OSError):
        pass
    return {"period": None, "updated_at": 0, "peers": {}}


def save_state(path, state):
    directory = os.path.dirname(path) or "."
    os.makedirs(directory, mode=0o700, exist_ok=True)
    descriptor, temp_path = tempfile.mkstemp(prefix=".traffic-", dir=directory, text=True)
    try:
        if hasattr(os, "fchmod"):
            os.fchmod(descriptor, 0o600)
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump(state, handle, ensure_ascii=False, indent=2)
            handle.write("\n")
        os.replace(temp_path, path)
    finally:
        if os.path.exists(temp_path):
            try:
                os.remove(temp_path)
            except OSError:
                pass


def main():
    args = parse_args()
    tz = get_timezone(args.timezone)
    now = datetime.now(tz) if tz else datetime.now().astimezone()
    period = now.strftime("%Y-%m")

    config_peers = load_peers(args.config)
    wg_stats = parse_wg_dump(read_wg_dump(args))
    state = load_state(args.state)

    if state.get("period") != period:
        # 跨月/首次运行：清零，并以当前计数器作为新基线。
        state = {"period": period, "updated_at": int(time.time()), "peers": {}}

    previous = state.get("peers", {})
    if not isinstance(previous, dict):
        previous = {}
    next_peers = {}

    for name, config_peer in config_peers.items():
        public_key = config_peer["public_key"]
        token = config_peer.get("token", "")
        current = wg_stats.get(public_key)
        old = previous.get(name)

        if current is None:
            # 这个 peer 当前不在 wg dump 中（例如接口刚重启），保留已有累计。
            if old and old.get("public_key") == public_key:
                old["token"] = token
                next_peers[name] = old
            continue

        if old is None or old.get("public_key") != public_key:
            next_peers[name] = {
                "public_key": public_key,
                "token": token,
                "rx": 0,
                "tx": 0,
                "last_rx": current["rx"],
                "last_tx": current["tx"],
            }
            continue

        last_rx = int(old.get("last_rx", 0))
        last_tx = int(old.get("last_tx", 0))
        rx_delta = current["rx"] - last_rx if current["rx"] >= last_rx else current["rx"]
        tx_delta = current["tx"] - last_tx if current["tx"] >= last_tx else current["tx"]

        old["token"] = token
        old["rx"] = int(old.get("rx", 0)) + max(0, rx_delta)
        old["tx"] = int(old.get("tx", 0)) + max(0, tx_delta)
        old["last_rx"] = current["rx"]
        old["last_tx"] = current["tx"]
        next_peers[name] = old

    state["period"] = period
    state["updated_at"] = int(time.time())
    state["peers"] = next_peers
    save_state(args.state, state)
    print("period=%s peers=%d state=%s" % (period, len(next_peers), args.state))


if __name__ == "__main__":
    main()
