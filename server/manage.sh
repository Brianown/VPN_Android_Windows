#!/usr/bin/env bash
#
# LiteTunnel（轻隧）服务端管理命令
# 安装后会被复制到 /usr/local/bin/litetunnel
#
# 用法：sudo litetunnel <命令> [参数]
#
set -Eeuo pipefail

ENV_FILE="${LITETUNNEL_ENV:-/etc/wireguard/litetunnel.env}"
if [[ -r ${ENV_FILE} ]]; then
    # shellcheck disable=SC1090
    source "${ENV_FILE}"
fi

WG_IF="${WG_IF:-wg0}"
WG_PORT="${WG_PORT:-51820}"
WG_NET="${WG_NET:-10.66.66}"
WG_DNS="${WG_DNS:-1.1.1.1, 8.8.8.8}"
WG_MTU="${WG_MTU:-1420}"
WG_ENDPOINT="${WG_ENDPOINT:-}"
CLIENT_DIR="${CLIENT_DIR:-/root/litetunnel/clients}"
STATS_PORT="${STATS_PORT:-8080}"
STATS_TZ="${STATS_TZ:-Asia/Shanghai}"
SERVER_LOCATION="${SERVER_LOCATION:-未知}"
SERVER_TYPE="${SERVER_TYPE:-服务器}"
PROBE_PORT="${PROBE_PORT:-8443}"
CONF="/etc/wireguard/${WG_IF}.conf"

PYTHON_BIN="$(command -v python3 || command -v python3.6 || command -v python || true)"

if [[ -t 1 ]]; then
    C_RESET=$'\033[0m'; C_GREEN=$'\033[32m'
    C_YELLOW=$'\033[33m'; C_RED=$'\033[31m'; C_CYAN=$'\033[36m'
else
    C_RESET=''; C_GREEN=''; C_YELLOW=''; C_RED=''; C_CYAN=''
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

usage() {
    cat <<EOF
LiteTunnel（轻隧）服务端管理工具

用法：sudo litetunnel <命令> [参数]

命令：
  add <名称>       添加一个客户端（自动生成密钥，名称限字母/数字/下划线/短横线，最长 15 位）
  add-pubkey <名称> <公钥>
                   用已有的客户端公钥添加 peer（不生成客户端私钥）
  export [--quiet] 生成并打印【所有客户端需要导入的设置】（含私钥，注意保密；--quiet 只写文件）
  server-key       显示服务器公钥、Endpoint 和虚拟网段
  list             列出客户端、IP、最近握手和流量
  show             显示 wg 运行状态
  qr <名称>        在终端显示客户端二维码
  config <名称>    打印客户端完整配置
  del <名称>       删除并吊销客户端
  status           查看服务和端口状态
  traffic          查看服务端当前月度流量 JSON
  uninstall        卸载 LiteTunnel（保留已安装的系统软件包）
  help             显示本帮助

示例：
  sudo litetunnel add phone
  sudo litetunnel export
  sudo litetunnel qr phone
  sudo litetunnel list
EOF
}

require_install() {
    [[ -f ${CONF} ]] || die "未找到 ${CONF}。请先运行 server/install.sh 安装 LiteTunnel。"
    command -v wg >/dev/null 2>&1 || die "未找到 wg 命令，请安装 wireguard-tools。"
}

require_root() {
    [[ ${EUID} -eq 0 ]] || die "请使用 root 运行：sudo litetunnel $*"
}

valid_name() {
    [[ $1 =~ ^[a-zA-Z0-9_-]{1,15}$ ]]
}

server_public_key() {
    local private
    private="$(awk -F' = ' '/^PrivateKey = / { print $2; exit }' "${CONF}")"
    [[ -n ${private} ]] || die "无法从 ${CONF} 读取服务端私钥。"
    printf '%s' "${private}" | wg pubkey
}

next_ip() {
    local used ip
    used="$(grep -E '^AllowedIPs = ' "${CONF}" \
        | sed -nE 's#^AllowedIPs = .*\.([0-9]+)/32$#\1#p' \
        | sort -n || true)"
    for ip in $(seq 2 254); do
        if ! grep -qx "${ip}" <<< "${used}"; then
            printf '%s' "${ip}"
            return 0
        fi
    done
    die "虚拟网段 ${WG_NET}.0/24 中没有可用 IP 了。"
}

human_bytes() {
    local bytes="${1:-0}"
    awk -v b="${bytes}" 'BEGIN {
        if (b < 0) b = 0;
        split("B KB MB GB TB", unit, " ");
        i = 1;
        while (b >= 1024 && i < 5) { b /= 1024; i++ }
        if (i == 1) printf "%d %s", b, unit[i];
        else printf "%.1f %s", b, unit[i];
    }'
}

human_handshake() {
    local ts="${1:-0}" now diff
    now="$(date +%s)"
    if [[ -z ${ts} || ${ts} == 0 ]]; then
        printf '从未'
        return 0
    fi
    diff=$((now - ts))
    if (( diff < 60 )); then
        printf '%s 秒前' "${diff}"
    elif (( diff < 3600 )); then
        printf '%s 分钟前' "$((diff / 60))"
    elif (( diff < 86400 )); then
        printf '%s 小时前' "$((diff / 3600))"
    else
        printf '%s 天前' "$((diff / 86400))"
    fi
}

cmd_add() {
    require_root "add"
    require_install
    local name="$1" ip private public psk token
    valid_name "${name}" || die "名称不合法。只能使用字母、数字、下划线、短横线，长度 1-15。"
    if grep -q "^# litetunnel: name=${name}$" "${CONF}"; then
        die "客户端 ${name} 已存在。"
    fi

    ip="$(next_ip)"
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

    mkdir -p "${CLIENT_DIR}"
    chmod 700 "${CLIENT_DIR}"
    cat > "${CLIENT_DIR}/${name}.conf" <<EOF
# Name = ${name}
# ServerIP = ${WG_ENDPOINT}
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
PublicKey = $(server_public_key)
PresharedKey = ${psk}
Endpoint = ${WG_ENDPOINT}:${WG_PORT}
AllowedIPs = 0.0.0.0/0
PersistentKeepalive = 25
EOF
    chmod 600 "${CLIENT_DIR}/${name}.conf"
    if command -v qrencode >/dev/null 2>&1; then
        qrencode -o "${CLIENT_DIR}/${name}.png" -r "${CLIENT_DIR}/${name}.conf" -s 8 -m 2
    fi

    if systemctl is-active --quiet "wg-quick@${WG_IF}" 2>/dev/null; then
        if ! wg set "${WG_IF}" peer "${public}" \
            preshared-key <(printf '%s\n' "${psk}") \
            allowed-ips "${WG_NET}.${ip}/32" 2>/dev/null; then
            warn "客户端已写入配置，但即时生效失败；重启后生效：systemctl restart wg-quick@${WG_IF}"
        fi
    else
        warn "WireGuard 当前未运行，客户端将在服务启动后生效。"
    fi

    if systemctl is-active --quiet litetunnel-traffic.timer 2>/dev/null; then
        systemctl start litetunnel-traffic.service >/dev/null 2>&1 || true
    fi

    ok "已添加客户端：${name}"
    cat <<EOF
  IP        : ${WG_NET}.${ip}
  配置文件  : ${CLIENT_DIR}/${name}.conf
  二维码    : ${CLIENT_DIR}/${name}.png
  终端二维码: sudo litetunnel qr ${name}
EOF
}

cmd_export() {
    require_root "export"
    require_install
    local quiet=0
    if [[ ${1:-} == "--quiet" ]]; then
        quiet=1
    fi
    mkdir -p "${CLIENT_DIR}"
    chmod 700 "${CLIENT_DIR}"

    local out="${CLIENT_DIR}/import-all.txt"
    local bundle="${CLIENT_DIR}/litetunnel-clients.tar.gz"
    local file name found=0

    {
        printf '============================\n'
        printf '所有客户端需要导入的设置。\n'
        printf '============================\n'
        printf '服务器: %s:%s\n' "${WG_ENDPOINT}" "${WG_PORT}"
        printf '说明: 从每个客户端的 [Interface] 开始复制到文件末尾，粘贴到客户端即可。\n'
        printf '      以下内容包含客户端私钥，请通过安全渠道传输。\n\n'
        for file in "${CLIENT_DIR}"/*.conf; do
            [[ -e ${file} ]] || continue
            found=1
            name="$(basename "${file}" .conf)"
            printf '%s\n' "----- ${name} -----"
            cat "${file}"
            printf '\n'
        done
    } > "${out}"
    chmod 600 "${out}"

    if (( found == 0 )); then
        warn "没有找到客户端配置；请先执行 sudo litetunnel add <名称>。"
        cat "${out}"
        return 0
    fi

    if command -v tar >/dev/null 2>&1; then
        local tmp_bundle
        tmp_bundle="$(mktemp)"
        if tar -czf "${tmp_bundle}" --exclude='import-all.txt' -C "${CLIENT_DIR}" . 2>/dev/null; then
            chmod 600 "${tmp_bundle}"
            mv "${tmp_bundle}" "${bundle}"
        else
            rm -f "${tmp_bundle}"
        fi
    fi

    ok "已生成客户端导入内容"
    printf '  文本文件: %s\n' "${out}"
    [[ -f ${bundle} ]] && printf '  打包文件: %s\n' "${bundle}"
    if (( quiet == 0 )); then
        printf '\n'
        cat "${out}"
    fi
}

cmd_server_key() {
    require_root "server-key"
    require_install
    printf '服务器公钥 : %s\n' "$(server_public_key)"
    printf 'Endpoint   : %s:%s\n' "${WG_ENDPOINT}" "${WG_PORT}"
    printf '服务器位置 : %s（%s）\n' "${SERVER_LOCATION}" "${SERVER_TYPE}"
    printf '延迟探测   : TCP %s\n' "${PROBE_PORT}"
    printf '虚拟网段   : %s.0/24\n' "${WG_NET}"
    printf '服务端配置 : %s\n' "${CONF}"
}

cmd_add_pubkey() {
    require_root "add-pubkey"
    require_install
    local name="$1" pubkey="$2" ip psk token peer_file
    valid_name "${name}" || die "名称不合法。只能使用字母、数字、下划线、短横线，长度 1-15。"
    [[ ${pubkey} =~ ^[A-Za-z0-9+/]{43}=$ ]] || die "公钥格式不正确，应为 44 个字符的 WireGuard base64 公钥。"
    if grep -q "^# litetunnel: name=${name}$" "${CONF}"; then
        die "客户端 ${name} 已存在。"
    fi

    ip="$(next_ip)"
    psk="$(wg genpsk)"
    token="$(gen_token)"
    mkdir -p "${CLIENT_DIR}"
    chmod 700 "${CLIENT_DIR}"

    cat >> "${CONF}" <<EOF

[Peer]
# litetunnel: name=${name}
# litetunnel: token=${token}
PublicKey = ${pubkey}
PresharedKey = ${psk}
AllowedIPs = ${WG_NET}.${ip}/32
EOF

    if systemctl is-active --quiet "wg-quick@${WG_IF}" 2>/dev/null; then
        if ! wg set "${WG_IF}" peer "${pubkey}" \
            preshared-key <(printf '%s\n' "${psk}") \
            allowed-ips "${WG_NET}.${ip}/32" 2>/dev/null; then
            warn "客户端已写入配置，但即时生效失败；重启后生效：systemctl restart wg-quick@${WG_IF}"
        fi
    else
        warn "WireGuard 当前未运行，客户端将在服务启动后生效。"
    fi

    peer_file="${CLIENT_DIR}/${name}.peer.txt"
    cat > "${peer_file}" <<EOF
# 客户端 ${name} 的 Peer 信息
# 你已有自己的客户端私钥；请把下面的内容合并到你的 WireGuard 配置中。
# ServerIP = ${WG_ENDPOINT}
# Location = ${SERVER_LOCATION}
# ServerType = ${SERVER_TYPE}
# ProbePort = ${PROBE_PORT}
# StatsURL = http://${WG_NET}.1:${STATS_PORT}/api/traffic
# StatsToken = ${token}
[Interface]
Address = ${WG_NET}.${ip}/24
DNS = ${WG_DNS}
MTU = ${WG_MTU}
# PrivateKey = <在这里填写你自己的客户端私钥>

[Peer]
PublicKey = $(server_public_key)
PresharedKey = ${psk}
Endpoint = ${WG_ENDPOINT}:${WG_PORT}
AllowedIPs = 0.0.0.0/0
PersistentKeepalive = 25
EOF
    chmod 600 "${peer_file}"

    if systemctl is-active --quiet litetunnel-traffic.timer 2>/dev/null; then
        systemctl start litetunnel-traffic.service >/dev/null 2>&1 || true
    fi

    ok "已用公钥添加客户端：${name}"
    printf '  IP        : %s\n' "${WG_NET}.${ip}"
    printf '  Peer 信息 : %s\n\n' "${peer_file}"
    cat "${peer_file}"
}

cmd_list() {
    require_root "list"
    require_install

    local handshakes transfers rows
    handshakes="$(wg show "${WG_IF}" latest-handshakes 2>/dev/null || true)"
    transfers="$(wg show "${WG_IF}" transfer 2>/dev/null || true)"
    rows="$(awk '
        /^\[Peer\]/ { inpeer = 1; name = ""; pub = ""; ip = ""; next }
        inpeer && /^# litetunnel: name=/ { name = substr($0, index($0, "name=") + 5) }
        inpeer && /^PublicKey = / { pub = $3 }
        inpeer && /^AllowedIPs = / { ip = $3 }
        inpeer && pub != "" && ip != "" {
            printf "%s|%s|%s\n", name, ip, pub
            inpeer = 0
        }
    ' "${CONF}")"

    if [[ -z ${rows} ]]; then
        info "还没有客户端。使用 sudo litetunnel add <名称> 添加。"
        return 0
    fi

    printf '%-16s %-16s %-14s %s\n' "名称" "IP" "最近握手" "流量（接收 / 发送）"
    while IFS='|' read -r name ip pub; do
        [[ -n ${pub} ]] || continue
        ip="${ip%/32}"
        local hs rx tx transfer
        hs="$(awk -v p="${pub}" '$1 == p { print $2; exit }' <<< "${handshakes}")"
        transfer="$(awk -v p="${pub}" '$1 == p { print $2, $3; exit }' <<< "${transfers}")"
        rx=""; tx=""
        if [[ -n ${transfer} ]]; then
            rx="${transfer%% *}"
            tx="${transfer##* }"
        fi
        printf '%-16s %-16s %-14s %s / %s\n' \
            "${name:--}" "${ip}" "$(human_handshake "${hs:-0}")" \
            "$(human_bytes "${rx:-0}")" "$(human_bytes "${tx:-0}")"
    done <<< "${rows}"
}

cmd_show() {
    require_root "show"
    require_install
    wg show "${WG_IF}"
}

cmd_qr() {
    require_root "qr"
    local name="$1"
    valid_name "${name}" || die "客户端名称不合法。"
    local file="${CLIENT_DIR}/${name}.conf"
    [[ -f ${file} ]] || die "找不到客户端配置：${file}"
    command -v qrencode >/dev/null 2>&1 || die "未安装 qrencode，请执行 apt-get install -y qrencode"
    qrencode -t ANSIUTF8 < "${file}"
}

cmd_config() {
    require_root "config"
    local name="$1"
    valid_name "${name}" || die "客户端名称不合法。"
    local file="${CLIENT_DIR}/${name}.conf"
    [[ -f ${file} ]] || die "找不到客户端配置：${file}"
    cat "${file}"
}

cmd_del() {
    require_root "del"
    require_install
    local name="$1" pub tmp
    valid_name "${name}" || die "客户端名称不合法。"

    pub="$(awk -v marker="# litetunnel: name=${name}" '
        $0 == marker { found = 1; next }
        found && /^PublicKey = / { print $3; exit }
    ' "${CONF}")"
    [[ -n ${pub} ]] || die "未找到客户端 ${name}。"

    if systemctl is-active --quiet "wg-quick@${WG_IF}" 2>/dev/null; then
        wg set "${WG_IF}" peer "${pub}" remove 2>/dev/null || true
    fi

    tmp="$(mktemp)"
    chmod 600 "${tmp}"
    awk -v RS='' -v ORS='\n\n' -v marker="# litetunnel: name=${name}" '
        {
            keep = 1
            n = split($0, lines, "\n")
            for (i = 1; i <= n; i++)
                if (lines[i] == marker) keep = 0
            if (keep) print
        }
    ' "${CONF}" > "${tmp}"
    cat "${tmp}" > "${CONF}"
    rm -f "${tmp}"

    rm -f "${CLIENT_DIR}/${name}.conf" "${CLIENT_DIR}/${name}.png" "${CLIENT_DIR}/${name}.peer.txt"
    if systemctl is-active --quiet litetunnel-traffic.timer 2>/dev/null; then
        systemctl start litetunnel-traffic.service >/dev/null 2>&1 || true
    fi
    ok "已删除并吊销客户端：${name}"
}

cmd_traffic() {
    require_root "traffic"
    require_install
    local file="/var/lib/litetunnel/traffic.json"
    [[ -f ${file} ]] || die "流量统计文件尚未生成，请检查 litetunnel-traffic.service。"
    [[ -n ${PYTHON_BIN} ]] || die "找不到 python3/python。"
    "${PYTHON_BIN}" -m json.tool "${file}"
}

cmd_status() {
    require_root "status"
    require_install
    if systemctl is-active --quiet "wg-quick@${WG_IF}" 2>/dev/null; then
        ok "wg-quick@${WG_IF} 正在运行"
    else
        warn "wg-quick@${WG_IF} 未运行"
    fi
    wg show "${WG_IF}" 2>/dev/null || true
    if systemctl is-active --quiet litetunnel-api.service 2>/dev/null; then
        ok "流量统计 API 正在运行"
    else
        warn "流量统计 API 未运行（客户端会显示：暂时无法获取）"
    fi
    if systemctl is-active --quiet litetunnel-probe.service 2>/dev/null; then
        ok "延迟探测服务正在运行（TCP ${PROBE_PORT}）"
    else
        warn "延迟探测服务未运行（客户端延迟会显示 —）"
    fi
    if command -v ss >/dev/null 2>&1; then
        printf '\nUDP %s 监听状态：\n' "${WG_PORT}"
        ss -lunp 2>/dev/null | grep -E ":${WG_PORT}[[:space:]]" || warn "未看到监听（可能需要 root 权限查看）"
    fi
}

cmd_uninstall() {
    require_root "uninstall"
    require_install
    warn "即将停止 WireGuard、删除 ${CONF}、客户端配置和 /usr/local/bin/litetunnel。"
    local answer
    read -r -p "确认卸载请输入 yes： " answer
    if [[ ${answer} != "yes" ]]; then
        info "已取消。"
        exit 0
    fi
    systemctl disable --now litetunnel-api.service litetunnel-traffic.timer litetunnel-probe.service >/dev/null 2>&1 || true
    systemctl disable --now "wg-quick@${WG_IF}" >/dev/null 2>&1 || true
    rm -f "${CONF}" "${ENV_FILE}" /etc/sysctl.d/99-litetunnel.conf /usr/local/bin/litetunnel
    rm -f /etc/systemd/system/litetunnel-traffic.service \
        /etc/systemd/system/litetunnel-traffic.timer \
        /etc/systemd/system/litetunnel-api.service \
        /etc/systemd/system/litetunnel-probe.service
    rm -rf /usr/local/lib/litetunnel /var/lib/litetunnel /root/litetunnel
    systemctl daemon-reload
    ok "LiteTunnel 已卸载。WireGuard 软件包保留在系统中。"
}

main() {
    local cmd="${1:-help}"
    if [[ ${cmd} == "help" || ${cmd} == "-h" || ${cmd} == "--help" ]]; then
        usage
        exit 0
    fi
    shift || true
    require_root "${cmd}"
    case "${cmd}" in
        add)
            [[ $# -eq 1 ]] || die "用法：sudo litetunnel add <名称>"
            cmd_add "$1"
            ;;
        add-pubkey)
            [[ $# -eq 2 ]] || die "用法：sudo litetunnel add-pubkey <名称> <客户端公钥>"
            cmd_add_pubkey "$1" "$2"
            ;;
        export)
            cmd_export "${1:-}"
            ;;
        server-key)
            cmd_server_key
            ;;
        list)
            cmd_list
            ;;
        show)
            cmd_show
            ;;
        qr)
            [[ $# -eq 1 ]] || die "用法：sudo litetunnel qr <名称>"
            cmd_qr "$1"
            ;;
        config)
            [[ $# -eq 1 ]] || die "用法：sudo litetunnel config <名称>"
            cmd_config "$1"
            ;;
        del)
            [[ $# -eq 1 ]] || die "用法：sudo litetunnel del <名称>"
            cmd_del "$1"
            ;;
        status)
            cmd_status
            ;;
        traffic)
            cmd_traffic
            ;;
        uninstall)
            cmd_uninstall
            ;;
        *)
            die "未知命令：${cmd}。运行 sudo litetunnel help 查看帮助。"
            ;;
    esac
}

main "$@"
