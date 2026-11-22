#!/usr/bin/env python3
"""LiteTunnel 月度流量 HTTP API。

只建议监听 VPN 内网地址（例如 10.66.66.1:8080），通过客户端配置里的
随机 StatsToken 返回该客户端本自然月的累计流量。API 不会记录 token。

响应示例：
{
  "name": "client1",
  "period": "2026-11",
  "rx": 123456,
  "tx": 654321,
  "updated_at": 1760000000,
  "reset_at": "2026-12-01T00:00:00+08:00"
}
"""

import argparse
import hmac
import json
import socketserver
import sys
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import parse_qs, urlparse

try:
    from zoneinfo import ZoneInfo
except ImportError:  # Python < 3.9
    ZoneInfo = None


def get_timezone(name):
    if not name:
        return None
    if name in ("Asia/Shanghai", "PRC", "Asia/Chongqing", "Asia/Harbin"):
        return timezone(timedelta(hours=8), name="Asia/Shanghai")
    if ZoneInfo is not None:
        try:
            return ZoneInfo(name)
        except Exception:
            return None
    return None


def reset_at_iso(period, tz):
    try:
        year, month = [int(part) for part in period.split("-", 1)]
    except (ValueError, AttributeError):
        return None
    next_year = year + (1 if month == 12 else 0)
    next_month = 1 if month == 12 else month + 1
    moment = datetime(next_year, next_month, 1, 0, 0, 0, tzinfo=tz)
    return moment.isoformat()


class ThreadingHTTPServer(socketserver.ThreadingMixIn, HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


class TrafficHandler(BaseHTTPRequestHandler):
    state_path = "/var/lib/litetunnel/traffic.json"
    timezone_name = "Asia/Shanghai"
    server_version = "LiteTunnelTraffic/1.0"

    def do_GET(self):
        parsed = urlparse(self.path)
        if parsed.path != "/api/traffic":
            self._send_json(404, {"error": "not found"})
            return

        query = parse_qs(parsed.query)
        token = (query.get("token") or [""])[0]
        if not token:
            self._send_json(401, {"error": "missing token"})
            return

        try:
            with open(self.state_path, "r", encoding="utf-8") as handle:
                state = json.load(handle)
        except FileNotFoundError:
            self._send_json(503, {"error": "traffic stats not ready"})
            return
        except (ValueError, OSError):
            self._send_json(503, {"error": "traffic stats unavailable"})
            return

        peers = state.get("peers") or {}
        if not isinstance(peers, dict):
            self._send_json(503, {"error": "traffic stats invalid"})
            return

        matched_name = None
        matched = None
        for name, item in peers.items():
            stored = str(item.get("token", "")) if isinstance(item, dict) else ""
            if stored and hmac.compare_digest(stored, token):
                matched_name = name
                matched = item
                break

        if matched is None:
            self._send_json(404, {"error": "unknown client"})
            return

        period = str(state.get("period", ""))
        tz = get_timezone(self.timezone_name)
        payload = {
            "name": matched_name,
            "period": period,
            "rx": int(matched.get("rx", 0)),
            "tx": int(matched.get("tx", 0)),
            "updated_at": int(state.get("updated_at", 0)),
            "reset_at": reset_at_iso(period, tz),
        }
        self._send_json(200, payload)

    def _send_json(self, status, payload):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        # 不记录请求行，避免 token 出现在日志里。
        return


def main():
    parser = argparse.ArgumentParser(description="LiteTunnel traffic HTTP API")
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--state", default="/var/lib/litetunnel/traffic.json")
    parser.add_argument("--timezone", default="Asia/Shanghai")
    args = parser.parse_args()

    TrafficHandler.state_path = args.state
    TrafficHandler.timezone_name = args.timezone

    server = ThreadingHTTPServer((args.bind, args.port), TrafficHandler)
    print("LiteTunnel traffic API listening on %s:%d" % (args.bind, args.port))
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
