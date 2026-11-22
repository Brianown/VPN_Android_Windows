#!/usr/bin/env bash
#
# LiteTunnel（轻隧）服务端一键安装脚本
#
# 适用系统：Debian 11/12、Ubuntu 20.04/22.04/24.04（x86_64 / arm64）
# 需要：root 权限、systemd、可访问公网
#
# 用法（在仓库目录中）：
#   sudo bash server/install.sh
#
# 用法（一键，发布到 GitHub 后）：
#   curl -fsSL https://github.com/<你的用户名>/litetunnel/archive/refs/heads/main.tar.gz \
#     | tar xz -C /opt && sudo bash /opt/litetunnel-main/server/install.sh
#
# 可选环境变量：
#   WG_PORT=51820             WireGuard UDP 端口
#   WG_NET=10.66.66           虚拟网段（前三段）
#   WG_DNS="1.1.1.1, 8.8.8.8" 下发给客户端的 DNS
#   WG_MTU=1420               客户端 MTU
#   WG_CLIENTS=3              初始生成的客户端数量
#   WG_ENDPOINT=1.2.3.4       服务器公网 IP（默认自动探测，NAT 后面需手动指定）
#   CLIENT_PREFIX=client      客户端名前缀
#
set -Eeuo pipefail

WG_IF="${WG_IF:-wg0}"
WG_PORT="${WG_PORT:-51820}"
WG_NET="${WG_NET:-10.66.66}"
WG_DNS="${WG_DNS:-1.1.1.1, 8.8.8.8}"
WG_MTU="${WG_MTU:-1420}"
WG_CLIENTS="${WG_CLIENTS:-3}"
CLIENT_PREFIX="${CLIENT_PREFIX:-client}"

STATE_DIR="/etc/wireguard"
CONF="${STATE_DIR}/${WG_IF}.conf"
ENV_FILE="${STATE_DIR}/litetunnel.env"
CLIENT_DIR="/root/litetunnel/clients"

if [[ -t 1 ]]; then
    C_RESET=$'\033[0m'; C_BOLD=$'\033[1m'; C_GREEN=$'\033[32m'
    C_YELLOW=$'\033[33m'; C_RED=$'\033[31m'; C_CYAN=$'\033[36m'
else
    C_RESET=''; C_BOLD=''; C_GREEN=''; C_YELLOW=''; C_RED=''; C_CYAN=''
fi

info() { printf '%s[LiteTunnel]%s %s\n' "$C_CYAN" "$C_RESET" "$*"; }
ok()   { printf '%s[   OK   ]%s %s\n' "$C_GREEN" "$C_RESET" "$*"; }
warn() { printf '%s[  WARN  ]%s %s\n' "$C_YELLOW" "$C_RESET" "$*" >&2; }
die()  { printf '%s[ FAILED ]%s %s\n' "$C_RED" "$C_RESET" "$*" >&2; exit 1; }

trap 'die "脚本在第 ${LINENO} 行执行失败，已中止。请检查上面的错误信息。"' ERR

# ---------------------------------------------------------------- 基础检查

[[ ${EUID} -eq 0 ]] || die "请使用 root 运行：sudo bash server/install.sh"
command -v systemctl >/dev/null 2>&1 || die "未找到 systemd，本脚本仅支持使用 systemd 的发行版。"
[[ -f /etc/os-release ]] || die "无法识别操作系统（缺少 /etc/os-release）。"

# shellcheck disable=SC1091
. /etc/os-release
case "${ID:-}" in
    debian|ubuntu|raspbian|linuxmint|pop)
        ;;
    *)
        warn "当前系统为 ${PRETTY_NAME:-未知}，脚本只在 Debian/Ubuntu 上验证过，继续尝试……"
        ;;
esac

if [[ -f ${CONF} ]]; then
    die "检测到已存在的 ${CONF}。如需重新安装，请先执行：sudo litetunnel uninstall（或手动删除该文件）
     添加客户端请使用：sudo litetunnel add 手机名称"
fi

[[ ${WG_IF} =~ ^[a-zA-Z0-9_=+.-]{1,15}$ ]] || die "WG_IF 不合法：${WG_IF}"
[[ ${WG_PORT} =~ ^[0-9]+$ ]] || die "WG_PORT 必须是 1-65535 的数字"
(( 10#${WG_PORT} >= 1 && 10#${WG_PORT} <= 65535 )) || die "WG_PORT 必须是 1-65535 的数字"
[[ ${WG_NET} =~ ^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}$ ]] || die "WG_NET 格式应为 10.66.66 这样的前三段"
IFS='.' read -r NET_A NET_B NET_C <<< "${WG_NET}"
for octet in "${NET_A}" "${NET_B}" "${NET_C}"; do
    (( 10#${octet} <= 255 )) || die "WG_NET 的每一段必须在 0-255 之间：${WG_NET}"
done
[[ ${WG_CLIENTS} =~ ^[0-9]+$ ]] || die "WG_CLIENTS 必须是 1-200 的数字"
(( 10#${WG_CLIENTS} >= 1 && 10#${WG_CLIENTS} <= 200 )) || die "WG_CLIENTS 必须是 1-200 的数字"
[[ ${CLIENT_PREFIX} =~ ^[a-zA-Z0-9_-]{1,10}$ ]] || die "CLIENT_PREFIX 只能包含字母、数字、下划线、短横线，且不超过 10 个字符"

# ---------------------------------------------------------------- 安装依赖

info "更新软件源并安装依赖（wireguard-tools / qrencode / iptables）……"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends \
    wireguard-tools qrencode iptables iproute2 curl ca-certificates >/dev/null
ok "依赖安装完成"

modprobe wireguard 2>/dev/null || true
if ! ip link add litetunnel-test type wireguard 2>/dev/null; then
    info "当前内核没有内置 WireGuard，尝试安装 DKMS 模块（旧内核需要）……"
    apt-get install -y -qq --no-install-recommends \
        "linux-headers-$(uname -r)" wireguard-dkms >/dev/null 2>&1 || true
    modprobe wireguard 2>/dev/null || true
    if ! ip link add litetunnel-test type wireguard 2>/dev/null; then
        die "当前内核不支持 WireGuard。请升级内核，或安装 wireguard-dkms 与对应内核头文件后重试。"
    fi
fi
ip link del litetunnel-test 2>/dev/null || true

# ---------------------------------------------------------------- 网络检测

sysctl -w net.ipv4.ip_forward=1 >/dev/null
cat > /etc/sysctl.d/99-litetunnel.conf <<'SYSCTL'
# LiteTunnel: 允许内核转发，供 VPN 客户端访问公网
net.ipv4.ip_forward = 1
SYSCTL

WAN_IF="$(ip -4 route show default | awk '{for (i = 1; i < NF; i++) if ($i == "dev") { print $(i + 1); exit }}')"
[[ -n ${WAN_IF} ]] || die "无法检测默认出口网卡，请检查网络配置。"
info "默认出口网卡：${WAN_IF}"

detect_endpoint() {
    local ep=""
    local url
    for url in \
        "https://api.ipify.org" \
        "https://ifconfig.me/ip" \
        "https://ipv4.icanhazip.com"; do
        ep="$(curl -4 -fsS --connect-timeout 5 --max-time 8 "${url}" 2>/dev/null | tr -d '[:space:]' || true)"
        if [[ ${ep} =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
            printf '%s' "${ep}"
            return 0
        fi
    done
    ep="$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i = 1; i < NF; i++) if ($i == "src") { print $(i + 1); exit }}')"
    printf '%s' "${ep}"
}

ENDPOINT="${WG_ENDPOINT:-$(detect_endpoint)}"
[[ ${ENDPOINT} =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]] || die \
    "无法自动检测公网 IPv4。若服务器在 NAT 后面，请手动执行：
     sudo WG_ENDPOINT=你的公网IP bash server/install.sh"
ok "服务器公网地址：${ENDPOINT}:${WG_PORT}/udp"

# ---------------------------------------------------------------- 生成密钥

umask 077
install -d -m 700 "${STATE_DIR}"
install -d -m 700 /root/litetunnel
install -d -m 700 "${CLIENT_DIR}"

if [[ ! -s ${STATE_DIR}/server_private.key ]]; then
    wg genkey > "${STATE_DIR}/server_private.key"
fi
chmod 600 "${STATE_DIR}/server_private.key"
SERVER_PRIVATE="$(cat "${STATE_DIR}/server_private.key")"
SERVER_PUBLIC="$(printf '%s' "${SERVER_PRIVATE}" | wg pubkey)"
ok "服务端密钥已生成"

# ---------------------------------------------------------------- 服务端配置

info "写入 ${CONF} ……"
: > "${CONF}"
chmod 600 "${CONF}"

cat > "${CONF}" <<EOF
[Interface]
Address = ${WG_NET}.1/24
ListenPort = ${WG_PORT}
PrivateKey = ${SERVER_PRIVATE}
MTU = ${WG_MTU}
SaveConfig = false
PostUp = sysctl -w net.ipv4.ip_forward=1 >/dev/null
PostUp = iptables -C FORWARD -i %i -j ACCEPT 2>/dev/null || iptables -A FORWARD -i %i -j ACCEPT
PostUp = iptables -C FORWARD -o %i -j ACCEPT 2>/dev/null || iptables -A FORWARD -o %i -j ACCEPT
PostUp = iptables -t nat -C POSTROUTING -s ${WG_NET}.0/24 -o ${WAN_IF} -j MASQUERADE 2>/dev/null || iptables -t nat -A POSTROUTING -s ${WG_NET}.0/24 -o ${WAN_IF} -j MASQUERADE
PostDown = iptables -D FORWARD -i %i -j ACCEPT 2>/dev/null || true
PostDown = iptables -D FORWARD -o %i -j ACCEPT 2>/dev/null || true
PostDown = iptables -t nat -D POSTROUTING -s ${WG_NET}.0/24 -o ${WAN_IF} -j MASQUERADE 2>/dev/null || true
EOF

# ---------------------------------------------------------------- 客户端

add_client() {
    local name="$1"
    local ip="$2"
    local private public psk

    private="$(wg genkey)"
    public="$(printf '%s' "${private}" | wg pubkey)"
    psk="$(wg genpsk)"

    cat >> "${CONF}" <<EOF

[Peer]
# litetunnel: name=${name}
PublicKey = ${public}
PresharedKey = ${psk}
AllowedIPs = ${WG_NET}.${ip}/32
EOF

    cat > "${CLIENT_DIR}/${name}.conf" <<EOF
# Name = ${name}
[Interface]
PrivateKey = ${private}
Address = ${WG_NET}.${ip}/24
DNS = ${WG_DNS}
MTU = ${WG_MTU}

[Peer]
PublicKey = ${SERVER_PUBLIC}
PresharedKey = ${psk}
Endpoint = ${ENDPOINT}:${WG_PORT}
AllowedIPs = 0.0.0.0/0
PersistentKeepalive = 25
EOF
    chmod 600 "${CLIENT_DIR}/${name}.conf"
    if command -v qrencode >/dev/null 2>&1; then
        qrencode -o "${CLIENT_DIR}/${name}.png" -r "${CLIENT_DIR}/${name}.conf" -s 8 -m 2
    fi
}

CLIENT_NAMES=()
info "生成 ${WG_CLIENTS} 个客户端配置……"
for ((i = 1; i <= WG_CLIENTS; i++)); do
    name="${CLIENT_PREFIX}${i}"
    add_client "${name}" "$((i + 1))"
    CLIENT_NAMES+=("${name}")
done
ok "客户端配置已生成：${CLIENT_DIR}"

# ---------------------------------------------------------------- 启动服务

info "启动 WireGuard 服务 wg-quick@${WG_IF} ……"
if ! systemctl enable --now "wg-quick@${WG_IF}" >/dev/null 2>&1; then
    warn "服务启动失败，最近日志如下："
    journalctl -u "wg-quick@${WG_IF}" -n 40 --no-pager || true
    die "请根据日志排查（常见原因：UDP 端口被占用、iptables 规则冲突、内核模块缺失）。"
fi

systemctl is-active --quiet "wg-quick@${WG_IF}" || die "WireGuard 服务未处于运行状态。"
wg show "${WG_IF}" >/dev/null
ok "WireGuard 已启动并监听 UDP ${WG_PORT}"

# ---------------------------------------------------------------- 防火墙

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw allow "${WG_PORT}/udp" >/dev/null 2>&1 || true
    ok "已放行 UFW UDP ${WG_PORT}"
fi

if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --permanent --add-port="${WG_PORT}/udp" >/dev/null 2>&1 || true
    firewall-cmd --reload >/dev/null 2>&1 || true
    ok "已放行 firewalld UDP ${WG_PORT}"
fi

# ---------------------------------------------------------------- 管理命令

cat > "${ENV_FILE}" <<EOF
WG_IF=${WG_IF}
WG_PORT=${WG_PORT}
WG_NET=${WG_NET}
WG_DNS="${WG_DNS}"
WG_MTU=${WG_MTU}
WG_ENDPOINT=${ENDPOINT}
CLIENT_DIR=${CLIENT_DIR}
EOF
chmod 600 "${ENV_FILE}"

MANAGE_SRC=""
if [[ -n ${BASH_SOURCE[0]:-} && -f ${BASH_SOURCE[0]} ]]; then
    candidate="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/manage.sh"
    [[ -f ${candidate} ]] && MANAGE_SRC="${candidate}"
fi
if [[ -z ${MANAGE_SRC} && -n ${LITETUNNEL_RAW:-} ]]; then
    curl -fsSL "${LITETUNNEL_RAW%/}/server/manage.sh" -o /tmp/litetunnel-manage.sh
    MANAGE_SRC=/tmp/litetunnel-manage.sh
fi
if [[ -n ${MANAGE_SRC} ]]; then
    install -m 755 "${MANAGE_SRC}" /usr/local/bin/litetunnel
    ok "管理命令已安装：/usr/local/bin/litetunnel"
else
    warn "未找到 manage.sh，可手动使用仓库中的 server/manage.sh。"
fi

# ---------------------------------------------------------------- 完成信息

printf '\n%s%s 安装完成！%s\n\n' "${C_GREEN}" "${C_BOLD}" "${C_RESET}"
cat <<EOF
服务器公网地址 : ${ENDPOINT}
WireGuard 端口 : ${WG_PORT}/udp
虚拟网段       : ${WG_NET}.0/24
服务端配置     : ${CONF}
客户端配置目录 : ${CLIENT_DIR}
EOF

printf '\n已生成客户端：\n'
for idx in "${!CLIENT_NAMES[@]}"; do
    name="${CLIENT_NAMES[$idx]}"
    printf '  %-10s IP: %-14s 配置: %s/%s.conf  二维码: %s/%s.png\n' \
        "${name}" "${WG_NET}.$((idx + 2))" "${CLIENT_DIR}" "${name}" "${CLIENT_DIR}" "${name}"
done

cat <<EOF

${C_BOLD}下一步：${C_RESET}
  1. 把客户端 .conf 文件下载到手机/电脑，或直接用手机扫描 ${CLIENT_DIR}/*.png 二维码；
  2. Android：安装轻隧 APK，点右下角 + → 扫码或粘贴配置 → 点节点连接；
  3. Windows：安装 WireGuard for Windows，打开轻隧客户端导入 .conf 后连接；
  4. 再添加客户端：sudo litetunnel add 手机名称

${C_BOLD}重要：${C_RESET}
  * 云服务器（阿里云/腾讯云/AWS/Oracle 等）还需在控制台的安全组/防火墙中放行 UDP ${WG_PORT}；
  * Oracle Cloud 默认还有实例内 iptables 规则，必要时手动放行；
  * 客户端私钥只保存在本机，请勿把 .conf 或二维码发给别人；
  * 中国大陆网络对 UDP 的干扰较严重，WireGuard 可能不稳定；
    对抗封锁建议使用仓库中 docs/xray-reality.md 的 Xray REALITY 方案。
EOF
