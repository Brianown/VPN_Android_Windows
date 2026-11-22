#!/usr/bin/env python3
"""LiteTunnel TCP 延迟探测服务。

客户端在建立 VPN 之前，对服务器的探测端口做一次 TCP 连接并测量耗时。
服务端只接受连接、读取最多 1 字节并立即关闭，不返回任何业务数据。
"""

import argparse
import socketserver


class ProbeHandler(socketserver.BaseRequestHandler):
    def handle(self):
        try:
            self.request.settimeout(1.0)
            self.request.recv(1)
        except OSError:
            pass


class ProbeServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True
    request_queue_size = 64


def main():
    parser = argparse.ArgumentParser(description="LiteTunnel TCP latency probe")
    parser.add_argument("--port", type=int, default=8443)
    parser.add_argument("--bind", default="0.0.0.0")
    args = parser.parse_args()

    with ProbeServer((args.bind, args.port), ProbeHandler) as server:
        print("LiteTunnel latency probe listening on %s:%d" % (args.bind, args.port))
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
