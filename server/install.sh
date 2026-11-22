#!/usr/bin/env bash
#
# LiteTunnel（轻隧）服务端一键安装脚本
#
# 适用系统：Debian 11/12、Ubuntu 20.04/22.04/24.04（x86_64 / arm64）
#           CentOS 7 请先阅读 docs/centos7.md，再使用 SKIP_DEPS=1 运行
# 需要：root 权限、systemd、可访问公网
#
# 用法（在仓库目录中）：
#   sudo bash server/install.sh
#
# 用法（一键，发布到 GitHub 后）：
#   curl -fsSL https://github.com/Brianown/VPN_Android_Windows/archive/refs/heads/main.tar.gz \
#     -o /tmp/vpn.tar.gz && sudo tar xzf /tmp/vpn.tar.gz -C /opt \
#     && sudo bash /opt/VPN_Android_Windows-main/server/install.sh
#
# 可选环境变量：
#   WG_PORT=51820             WireGuard UDP 端口
#   WG_NET=10.66.66           虚拟网段（前三段）
#   WG_DNS="1.1.1.1, 8.8.8.8" 下发给客户端的 DNS
#   WG_MTU=1420               客户端 MTU
#   WG_CLIENTS=3              初始生成的客户端数量
#   WG_ENDPOINT=1.2.3.4       服务器公网 IP（默认自动探测，NAT 后面需手动指定）
#   CLIENT_PREFIX=client      客户端名前缀
#   WG_PRINT_CONFIGS=1        安装完成后打印所有客户端导入内容（含私钥；设为 0 关闭）
#   STATS_PORT=8080           客户端查询月度流量的 API 端口（只监听 VPN 内网）
#   STATS_TZ=Asia/Shanghai    月度流量清零使用的时区
#   SERVER_LOCATION=...       客户端展示的机房位置（默认用 ip-api.com 自动探测）
#   SERVER_TYPE=服务器        客户端展示的类型，可设为 家用
#   PROBE_PORT=8443           客户端测延迟用的公网 TCP 端口
#   SKIP_DEPS=1               跳过系统包安装（CentOS 7 等非 Debian 系统请先手动装依赖）
#
set -Eeuo pipefail

WG_IF="${WG_IF:-wg0}"
WG_PORT="${WG_PORT:-51820}"
WG_NET="${WG_NET:-10.66.66}"
WG_DNS="${WG_DNS:-1.1.1.1, 8.8.8.8}"
WG_MTU="${WG_MTU:-1420}"
WG_CLIENTS="${WG_CLIENTS:-3}"
CLIENT_PREFIX="${CLIENT_PREFIX:-client}"
WG_PRINT_CONFIGS="${WG_PRINT_CONFIGS:-1}"
STATS_PORT="${STATS_PORT:-8080}"
STATS_TZ="${STATS_TZ:-Asia/Shanghai}"
SERVER_LOCATION="${SERVER_LOCATION:-}"
SERVER_TYPE="${SERVER_TYPE:-服务器}"
PROBE_PORT="${PROBE_PORT:-8443}"
SKIP_DEPS="${SKIP_DEPS:-0}"
PM=""
PKG=""

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

gen_token() {
    local token
    token="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
    [[ ${#token} -eq 32 ]] || die "生成 StatsToken 失败。"
    printf '%s' "${token}"
}

trap 'die "脚本在第 ${LINENO} 行执行失败，已中止。请检查上面的错误信息。"' ERR

# ---------------------------------------------------------------- 基础检查

[[ ${EUID} -eq 0 ]] || die "请使用 root 运行：sudo bash server/install.sh"
command -v systemctl >/dev/null 2>&1 || die "未找到 systemd，本脚本仅支持使用 systemd 的发行版。"
[[ -f /etc/os-release ]] || die "无法识别操作系统（缺少 /etc/os-release）。"

# shellcheck disable=SC1091
. /etc/os-release
case "${ID:-}" in
    debian|ubuntu|raspbian|linuxmint|pop|centos|rhel|rocky|almalinux|ol|fedora|opensuse*|sles|arch|manjaro)
        ;;
    *)
        warn "当前系统为 ${PRETTY_NAME:-未知}，将尝试自动适配包管理器；如果失败请手动安装依赖。"
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
[[ ${STATS_PORT} =~ ^[0-9]+$ ]] || die "STATS_PORT 必须是数字"
(( 10#${STATS_PORT} >= 1 && 10#${STATS_PORT} <= 65535 )) || die "STATS_PORT 必须是 1-65535 的数字"
[[ ${PROBE_PORT} =~ ^[0-9]+$ ]] || die "PROBE_PORT 必须是数字"
(( 10#${PROBE_PORT} >= 1 && 10#${PROBE_PORT} <= 65535 )) || die "PROBE_PORT 必须是 1-65535 的数字"
[[ ${PROBE_PORT} != "${WG_PORT}" ]] || die "PROBE_PORT 不能与 WireGuard UDP 端口相同"
[[ ${PROBE_PORT} != "${STATS_PORT}" ]] || die "PROBE_PORT 不能与 STATS_PORT 相同"

# ---------------------------------------------------------------- 安装依赖

detect_pm() {
    if command -v apt-get >/dev/null 2>&1; then
        PM=apt
    elif command -v dnf >/dev/null 2>&1; then
        PM=dnf
    elif command -v yum >/dev/null 2>&1; then
        PM=yum
    elif command -v zypper >/dev/null 2>&1; then
        PM=zypper
    elif command -v pacman >/dev/null 2>&1; then
        PM=pacman
    else
        PM=unknown
    fi
}

fix_centos7_repos() {
    [[ ${ID:-} == "centos" && ${VERSION_ID:-} == 7* ]] || return 0
    [[ -f /etc/yum.repos.d/CentOS-Vault.repo ]] && return 0

    info "CentOS 7 已 EOL，切换 yum 源到 vault.centos.org ……"
    mkdir -p /root/litetunnel-yum-backup
    local repo
    for repo in /etc/yum.repos.d/CentOS-*.repo; do
        [[ -e ${repo} ]] || continue
        mv "${repo}" /root/litetunnel-yum-backup/ 2>/dev/null || true
    done
    cat > /etc/yum.repos.d/CentOS-Vault.repo <<'EOF'
[base]
name=CentOS-7.9.2009 - Base
baseurl=http://vault.centos.org/7.9.2009/os/$basearch/
gpgcheck=1
gpgkey=file:///etc/pki/rpm-gpg/RPM-GPG-KEY-CentOS-7
enabled=1

[updates]
name=CentOS-7.9.2009 - Updates
baseurl=http://vault.centos.org/7.9.2009/updates/$basearch/
gpgcheck=1
gpgkey=file:///etc/pki/rpm-gpg/RPM-GPG-KEY-CentOS-7
enabled=1

[extras]
name=CentOS-7.9.2009 - Extras
baseurl=http://vault.centos.org/7.9.2009/extras/$basearch/
gpgcheck=1
gpgkey=file:///etc/pki/rpm-gpg/RPM-GPG-KEY-CentOS-7
enabled=1
EOF
    yum clean all >/dev/null 2>&1 || true
    yum makecache >/dev/null 2>&1 || warn "CentOS 7 vault 源 makecache 失败，后续安装可能失败。"
}

install_epel7() {
    yum install -y epel-release >/dev/null 2>&1 && return 0
    local epel_url
    epel_url="https://archives.fedoraproject.org/pub/archive/epel/7/x86_64/Packages/e/epel-release-7-14.noarch.rpm"
    rpm -Uvh --replacepkgs "${epel_url}" >/dev/null 2>&1 || \
        warn "EPEL 7 安装失败，qrencode 等包可能无法安装。"
}

install_elrepo() {
    rpm -q elrepo-release >/dev/null 2>&1 && return 0
    local el_major url
    el_major="$(rpm -E '%{rhel}' 2>/dev/null || true)"
    [[ ${el_major} =~ ^[0-9]+$ ]] || el_major="${VERSION_ID%%.*}"
    url="https://www.elrepo.org/elrepo-release-${el_major}.el${el_major}.elrepo.noarch.rpm"
    "${PKG}" install -y "${url}" >/dev/null 2>&1 || \
        warn "ELRepo 安装失败；如果内核没有 WireGuard 模块，需要手动安装 kmod-wireguard。"
}

install_dependencies() {
    detect_pm
    if [[ ${PM} == "unknown" ]]; then
        die "未识别的包管理器。支持 Debian/Ubuntu(apt)、RHEL/CentOS/Rocky/Alma/Fedora(dnf/yum)、
     openSUSE(zypper)、Arch/Manjaro(pacman)；请手动安装依赖后使用 SKIP_DEPS=1。"
    fi
    PKG="${PM}"
    info "检测到包管理器：${PM}"

    case "${PM}" in
        apt)
            export DEBIAN_FRONTEND=noninteractive
            apt-get update -qq
            apt-get install -y -qq --no-install-recommends \
                wireguard-tools iptables iproute2 python3 curl ca-certificates >/dev/null
            apt-get install -y -qq --no-install-recommends qrencode >/dev/null 2>&1 || \
                warn "qrencode 未安装，客户端二维码将不可用。"
            ;;
        dnf)
            if [[ ${ID:-} != "fedora" ]]; then
                local el_major
                el_major="$(rpm -E '%{rhel}' 2>/dev/null || true)"
                [[ ${el_major} =~ ^[0-9]+$ ]] || el_major="${VERSION_ID%%.*}"
                "${PKG}" install -y epel-release >/dev/null 2>&1 || \
                    "${PKG}" install -y "https://dl.fedoraproject.org/pub/epel/epel-release-latest-${el_major}.noarch.rpm" >/dev/null 2>&1 || true
                install_elrepo
            fi
            "${PKG}" install -y wireguard-tools iptables iproute python3 curl ca-certificates >/dev/null
            "${PKG}" install -y qrencode >/dev/null 2>&1 || warn "qrencode 未安装，客户端二维码将不可用。"
            if [[ ${ID:-} != "fedora" ]]; then
                "${PKG}" --enablerepo=elrepo install -y kmod-wireguard >/dev/null 2>&1 || true
            fi
            ;;
        yum)
            fix_centos7_repos
            install_epel7
            install_elrepo
            yum install -y wireguard-tools iptables iproute curl ca-certificates >/dev/null
            yum install -y python3 >/dev/null 2>&1 || \
                yum install -y python36 >/dev/null 2>&1 || die "无法安装 Python 3。"
            yum install -y qrencode >/dev/null 2>&1 || warn "qrencode 未安装，客户端二维码将不可用。"
            yum --enablerepo=elrepo install -y kmod-wireguard >/dev/null 2>&1 || true
            ;;
        zypper)
            zypper --non-interactive refresh >/dev/null 2>&1 || true
            zypper --non-interactive install wireguard-tools qrencode iptables iproute2 python3 curl ca-certificates >/dev/null
            ;;
        pacman)
            pacman -Sy --noconfirm --needed \
                wireguard-tools qrencode iptables-nft iproute2 python curl ca-certificates >/dev/null
            ;;
    esac
    ok "依赖安装完成"
}

ensure_wireguard_module() {
    local allow_install="$1"
    modprobe wireguard 2>/dev/null || true
    if ip link add litetunnel-test type wireguard 2>/dev/null; then
        ip link del litetunnel-test 2>/dev/null || true
        return 0
    fi

    if [[ ${allow_install} == "1" ]]; then
        case "${PM}" in
            apt)
                info "当前内核没有内置 WireGuard，尝试安装 DKMS 模块……"
                apt-get install -y -qq --no-install-recommends \
                    "linux-headers-$(uname -r)" wireguard-dkms >/dev/null 2>&1 || true
                ;;
            dnf|yum)
                if [[ ${ID:-} != "fedora" ]]; then
                    info "尝试安装 WireGuard DKMS 模块（需要对应内核头文件）……"
                    "${PKG}" install -y dkms gcc make \
                        "kernel-devel-$(uname -r)" "kernel-headers-$(uname -r)" >/dev/null 2>&1 || true
                    "${PKG}" install -y wireguard-dkms >/dev/null 2>&1 || true
                    if ! ip link add litetunnel-test type wireguard 2>/dev/null; then
                        if [[ ${PM} == "yum" ]]; then
                            "${PKG}" install -y yum-plugin-copr >/dev/null 2>&1 || true
                        else
                            "${PKG}" install -y dnf-plugins-core >/dev/null 2>&1 || true
                        fi
                        "${PKG}" copr enable -y jdoss/wireguard >/dev/null 2>&1 || true
                        "${PKG}" install -y wireguard-dkms >/dev/null 2>&1 || true
                    fi
                fi
                ;;
            zypper)
                zypper --non-interactive install wireguard-kmp-default >/dev/null 2>&1 || true
                ;;
        esac
        modprobe wireguard 2>/dev/null || true
    fi

    if ! ip link add litetunnel-test type wireguard 2>/dev/null; then
        die "当前内核没有可用的 WireGuard 模块。
     CentOS 7 / RHEL 请参考 docs/centos7.md 安装 kmod-wireguard 或 wireguard-dkms；
     其它系统请确认内核已启用 CONFIG_WIREGUARD，或安装 wireguard-dkms。"
    fi
    ip link del litetunnel-test 2>/dev/null || true
}

if [[ ${SKIP_DEPS} == "1" ]]; then
    info "SKIP_DEPS=1，跳过系统软件包安装；将检查所需命令是否存在。"
    detect_pm
else
    install_dependencies
fi

PYTHON_BIN="$(command -v python3 || command -v python3.6 || command -v python || true)"
[[ -n ${PYTHON_BIN} ]] || die "找不到 python3/python，请先安装 Python 3。"
for required in wg ip iptables curl systemctl; do
    command -v "${required}" >/dev/null 2>&1 || die "缺少命令 ${required}，请先安装依赖。"
done

ensure_wireguard_module "$([[ ${SKIP_DEPS} == "1" ]] && echo 0 || echo 1)"

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

detect_location() {
    local json location
    if [[ -n ${SERVER_LOCATION} ]]; then
        printf '%s' "${SERVER_LOCATION}"
        return 0
    fi
    json="$(curl -fsS --max-time 6 \
        "http://ip-api.com/json/${ENDPOINT}?fields=status,country,regionName,city&lang=zh-CN" \
        2>/dev/null || true)"
    [[ -n ${json} ]] || return 0
    location="$(printf '%s' "${json}" | "${PYTHON_BIN}" -c '
import json, sys
try:
    data = json.load(sys.stdin)
except Exception:
    sys.exit(0)
if data.get("status") != "success":
    sys.exit(0)
country = str(data.get("country") or "").strip()
city = str(data.get("city") or data.get("regionName") or "").strip()
print(" ".join(part for part in (country, city) if part))
' 2>/dev/null || true)"
    printf '%s' "${location}"
}

SERVER_LOCATION="$(detect_location)"
SERVER_LOCATION="${SERVER_LOCATION:-未知}"
SERVER_LOCATION="$(printf '%s' "${SERVER_LOCATION}" | tr -d '\r\n' | cut -c1-40)"
SERVER_TYPE="$(printf '%s' "${SERVER_TYPE}" | tr -d '\r\n' | cut -c1-20)"
[[ -n ${SERVER_TYPE} ]] || SERVER_TYPE="服务器"
ok "服务器位置：${SERVER_LOCATION}（${SERVER_TYPE}），延迟探测端口：TCP ${PROBE_PORT}"

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
    local private public psk token

    private="$(wg genkey)"
    public="$(printf '%s' "${private}" | wg pubkey)"
    psk="$(wg genpsk)"
    token="$(gen_token)"

    cat >> "${CONF}" <<EOF

[Peer]
# litetunnel: name=${name}
# litetunnel: token=${token}
PublicKey = ${public}
PresharedKey = ${psk}
AllowedIPs = ${WG_NET}.${ip}/32
EOF

    cat > "${CLIENT_DIR}/${name}.conf" <<EOF
# Name = ${name}
# ServerIP = ${ENDPOINT}
# Location = ${SERVER_LOCATION}
# ServerType = ${SERVER_TYPE}
# ProbePort = ${PROBE_PORT}
# StatsURL = http://${WG_NET}.1:${STATS_PORT}/api/traffic
# StatsToken = ${token}
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
    ufw allow "${PROBE_PORT}/tcp" >/dev/null 2>&1 || true
    ufw allow from "${WG_NET}.0/24" to any port "${STATS_PORT}" proto tcp >/dev/null 2>&1 || true
    ok "已放行 UFW UDP ${WG_PORT}、TCP ${PROBE_PORT}（延迟探测），以及 VPN 内网 TCP ${STATS_PORT}"
fi

if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --permanent --add-port="${WG_PORT}/udp" >/dev/null 2>&1 || true
    firewall-cmd --permanent --add-port="${PROBE_PORT}/tcp" >/dev/null 2>&1 || true
    firewall-cmd --permanent \
        --add-rich-rule="rule family=\"ipv4\" source address=\"${WG_NET}.0/24\" port port=\"${STATS_PORT}\" protocol=\"tcp\" accept" \
        >/dev/null 2>&1 || true
    firewall-cmd --reload >/dev/null 2>&1 || true
    ok "已放行 firewalld UDP ${WG_PORT}、TCP ${PROBE_PORT}（延迟探测），以及 VPN 内网 TCP ${STATS_PORT}"
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
STATS_PORT=${STATS_PORT}
STATS_TZ=${STATS_TZ}
SERVER_LOCATION="${SERVER_LOCATION}"
SERVER_TYPE="${SERVER_TYPE}"
PROBE_PORT=${PROBE_PORT}
EOF
chmod 600 "${ENV_FILE}"

SCRIPT_DIR=""
if [[ -n ${BASH_SOURCE[0]:-} && -f ${BASH_SOURCE[0]} ]]; then
    SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fi

MANAGE_SRC=""
if [[ -n ${SCRIPT_DIR} && -f ${SCRIPT_DIR}/manage.sh ]]; then
    MANAGE_SRC="${SCRIPT_DIR}/manage.sh"
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

# ---------------------------------------------------------------- 月度流量统计

STATS_SRC=""
API_SRC=""
PROBE_SRC=""
if [[ -n ${SCRIPT_DIR} && -f ${SCRIPT_DIR}/traffic-stats.py ]]; then
    STATS_SRC="${SCRIPT_DIR}/traffic-stats.py"
fi
if [[ -n ${SCRIPT_DIR} && -f ${SCRIPT_DIR}/traffic-api.py ]]; then
    API_SRC="${SCRIPT_DIR}/traffic-api.py"
fi
if [[ -n ${SCRIPT_DIR} && -f ${SCRIPT_DIR}/latency-probe.py ]]; then
    PROBE_SRC="${SCRIPT_DIR}/latency-probe.py"
fi
if [[ -z ${STATS_SRC} && -n ${LITETUNNEL_RAW:-} ]]; then
    curl -fsSL "${LITETUNNEL_RAW%/}/server/traffic-stats.py" -o /tmp/litetunnel-traffic-stats.py
    STATS_SRC=/tmp/litetunnel-traffic-stats.py
fi
if [[ -z ${API_SRC} && -n ${LITETUNNEL_RAW:-} ]]; then
    curl -fsSL "${LITETUNNEL_RAW%/}/server/traffic-api.py" -o /tmp/litetunnel-traffic-api.py
    API_SRC=/tmp/litetunnel-traffic-api.py
fi
if [[ -z ${PROBE_SRC} && -n ${LITETUNNEL_RAW:-} ]]; then
    curl -fsSL "${LITETUNNEL_RAW%/}/server/latency-probe.py" -o /tmp/litetunnel-latency-probe.py
    PROBE_SRC=/tmp/litetunnel-latency-probe.py
fi

if [[ -n ${STATS_SRC} && -n ${API_SRC} ]]; then
    install -d -m 755 /usr/local/lib/litetunnel
    install -d -m 700 /var/lib/litetunnel
    install -m 755 "${STATS_SRC}" /usr/local/lib/litetunnel/traffic-stats.py
    install -m 755 "${API_SRC}" /usr/local/lib/litetunnel/traffic-api.py

    cat > /etc/systemd/system/litetunnel-traffic.service <<EOF
[Unit]
Description=LiteTunnel monthly traffic collector
After=wg-quick@${WG_IF}.service
Wants=wg-quick@${WG_IF}.service

[Service]
Type=oneshot
ExecStart=${PYTHON_BIN} /usr/local/lib/litetunnel/traffic-stats.py --interface ${WG_IF} --config ${CONF} --state /var/lib/litetunnel/traffic.json --timezone ${STATS_TZ}
EOF

    cat > /etc/systemd/system/litetunnel-traffic.timer <<EOF
[Unit]
Description=Collect LiteTunnel traffic every minute

[Timer]
OnBootSec=30s
OnUnitActiveSec=60s
AccuracySec=5s
Unit=litetunnel-traffic.service

[Install]
WantedBy=timers.target
EOF

    cat > /etc/systemd/system/litetunnel-api.service <<EOF
[Unit]
Description=LiteTunnel traffic API (VPN interface only)
After=wg-quick@${WG_IF}.service
Requires=wg-quick@${WG_IF}.service

[Service]
Type=simple
ExecStart=${PYTHON_BIN} /usr/local/lib/litetunnel/traffic-api.py --bind ${WG_NET}.1 --port ${STATS_PORT} --state /var/lib/litetunnel/traffic.json --timezone ${STATS_TZ}
Restart=on-failure
RestartSec=3

[Install]
WantedBy=multi-user.target
EOF

    systemctl daemon-reload
    systemctl enable litetunnel-api.service litetunnel-traffic.timer >/dev/null 2>&1 || true
    systemctl start litetunnel-traffic.service >/dev/null 2>&1 || true
    systemctl start litetunnel-api.service >/dev/null 2>&1 || true
    systemctl start litetunnel-traffic.timer >/dev/null 2>&1 || true

    sleep 1
    if systemctl is-active --quiet litetunnel-api.service; then
        ok "月度流量统计 API 已启动：http://${WG_NET}.1:${STATS_PORT}/api/traffic（仅 VPN 内网）"
    else
        warn "流量统计 API 未能启动，请查看：journalctl -u litetunnel-api.service -n 40"
    fi

    first_conf="${CLIENT_DIR}/${CLIENT_NAMES[0]}.conf"
    first_token="$(sed -n 's/^# StatsToken = //p' "${first_conf}" | head -n 1)"
    if [[ -n ${first_token} ]]; then
        if curl -fsS --max-time 3 "http://${WG_NET}.1:${STATS_PORT}/api/traffic?token=${first_token}" >/dev/null 2>&1; then
            ok "流量统计 API 自检通过"
        else
            warn "流量统计 API 暂时不可用；客户端连接后会自动重试。"
        fi
    fi
else
    warn "未找到 traffic-stats.py / traffic-api.py，客户端将无法显示服务端月度流量。"
fi

# ---------------------------------------------------------------- 延迟探测

if [[ -n ${PROBE_SRC} ]]; then
    install -d -m 755 /usr/local/lib/litetunnel
    install -m 755 "${PROBE_SRC}" /usr/local/lib/litetunnel/latency-probe.py

    cat > /etc/systemd/system/litetunnel-probe.service <<EOF
[Unit]
Description=LiteTunnel TCP latency probe
After=network.target

[Service]
Type=simple
ExecStart=${PYTHON_BIN} /usr/local/lib/litetunnel/latency-probe.py --bind 0.0.0.0 --port ${PROBE_PORT}
Restart=on-failure
RestartSec=3

[Install]
WantedBy=multi-user.target
EOF

    systemctl daemon-reload
    systemctl enable --now litetunnel-probe.service >/dev/null 2>&1 || true
    if "${PYTHON_BIN}" -c "import socket; s = socket.create_connection(('127.0.0.1', ${PROBE_PORT}), 2); s.close()" 2>/dev/null; then
        ok "延迟探测服务已启动：TCP ${PROBE_PORT}"
    else
        warn "延迟探测服务未能启动，客户端延迟将显示为 —。"
    fi
else
    warn "未找到 latency-probe.py，客户端将无法显示服务器延迟。"
fi

# ---------------------------------------------------------------- 完成信息

printf '\n%s%s 安装完成！%s\n\n' "${C_GREEN}" "${C_BOLD}" "${C_RESET}"
cat <<EOF
服务器公网地址 : ${ENDPOINT}
WireGuard 端口 : ${WG_PORT}/udp
服务器公钥     : ${SERVER_PUBLIC}
服务器位置     : ${SERVER_LOCATION}（${SERVER_TYPE}）
延迟探测端口   : TCP ${PROBE_PORT}
虚拟网段       : ${WG_NET}.0/24
服务端配置     : ${CONF}
客户端配置目录 : ${CLIENT_DIR}
月度流量 API   : http://${WG_NET}.1:${STATS_PORT}/api/traffic（仅 VPN 内网）
流量统计周期   : 每月最后一天结束（次月 1 日 00:00，${STATS_TZ}）清零
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
  * 云服务器（阿里云/腾讯云/AWS/Oracle 等）还需在控制台的安全组/防火墙中放行 UDP ${WG_PORT} 和 TCP ${PROBE_PORT}；
  * Oracle Cloud 默认还有实例内 iptables 规则，必要时手动放行；
  * 客户端私钥只保存在本机，请勿把 .conf 或二维码发给别人；
  * 中国大陆网络对 UDP 的干扰较严重，WireGuard 可能不稳定；
    对抗封锁建议阅读仓库中的 docs/china.md。
EOF

# 打印【所有客户端需要导入的设置】，方便直接复制到手机/电脑。
if [[ -x /usr/local/bin/litetunnel ]]; then
    if [[ ${WG_PRINT_CONFIGS} == "1" ]]; then
        printf '\n%s%s 注意：以下内容包含客户端私钥 %s\n' "${C_YELLOW}" "${C_BOLD}" "${C_RESET}"
        printf '请勿在录屏、直播或公开日志中展示；不要截图发到公开群聊。\n\n'
        /usr/local/bin/litetunnel export || warn "自动导出失败，请手动执行：sudo litetunnel export"
    else
        /usr/local/bin/litetunnel export --quiet || warn "自动导出失败，请手动执行：sudo litetunnel export"
        printf '私钥内容已写入 %s/import-all.txt（未在终端打印）。\n' "${CLIENT_DIR}"
    fi
else
    warn "未找到 litetunnel 命令，无法自动导出；请使用 ${CLIENT_DIR} 下的 .conf 文件。"
fi
