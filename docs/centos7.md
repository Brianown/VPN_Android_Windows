# CentOS 7 / RHEL 7 安装说明

> ⚠️ CentOS 7 已于 2024-06-30 结束支持（EOL），官方 yum 镜像已下线，内核 3.10 也没有内置
> WireGuard。继续使用会面临安全更新缺失、软件源失效、内核模块兼容等问题。
>
> **最推荐的做法：在 VPS 控制台把系统重装为 Ubuntu 22.04/24.04、Debian 12，或 Rocky/Alma 9，
> 然后直接运行 README 里的一行安装命令。**
>
> 新版 `install.sh` 已经会尝试自动处理 CentOS 7（切换 vault 源、装 EPEL/ELRepo、kmod/DKMS）。
> 如果自动安装仍然失败，再按本页手动处理。

## 1. 切换到 CentOS Vault 源

CentOS 7 的 mirrorlist 已经不可用，需要改用 `vault.centos.org`：

```bash
mkdir -p /root/yum-backup
mv /etc/yum.repos.d/CentOS-*.repo /root/yum-backup/ 2>/dev/null || true

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

yum clean all
yum makecache
```

如果 `vault.centos.org` 访问慢，可以把 `http://` 换成可访问的镜像地址。

## 2. 安装 EPEL 和基础工具

```bash
yum install -y epel-release
yum install -y qrencode iptables iproute python3 curl ca-certificates
```

如果 `epel-release` 找不到，使用 EPEL 7 归档：

```bash
yum install -y https://archives.fedoraproject.org/pub/archive/epel/7/x86_64/Packages/e/epel-release-7-14.noarch.rpm
```

> EPEL 7 已归档，如果上面的版本号过期，去
> <https://archives.fedoraproject.org/pub/archive/epel/7/x86_64/Packages/e/> 找最新的
> `epel-release-7-*.noarch.rpm`。

## 3. 安装 WireGuard

### 方式 A：ELRepo 的 kmod（优先）

```bash
yum install -y https://www.elrepo.org/elrepo-release-7.el7.elrepo.noarch.rpm
yum --enablerepo=elrepo install -y kmod-wireguard wireguard-tools
```

检查：

```bash
modprobe wireguard
ip link add wgtest type wireguard && ip link del wgtest
```

如果提示 `Operation not supported` 或找不到模块，再用 DKMS。

### 方式 B：COPR DKMS（内核较旧/非标准内核）

```bash
yum install -y yum-plugin-copr
yum copr enable -y jdoss/wireguard
yum install -y dkms gcc make kernel-devel-$(uname -r) kernel-headers-$(uname -r)
yum install -y wireguard-dkms wireguard-tools
modprobe wireguard
ip link add wgtest type wireguard && ip link del wgtest
```

> DKMS 编译需要与 `uname -r` 完全一致的 `kernel-devel`。如果当前内核没有对应头文件，
> 先 `yum install -y kernel-devel` 并重启到新内核；这一步很容易失败，因此仍然建议重装系统。

## 4. 运行 LiteTunnel 安装

依赖已经手动安装，使用 `SKIP_DEPS=1` 跳过 apt：

```bash
curl -fsSL https://github.com/Brianown/VPN_Android_Windows/archive/refs/heads/main.tar.gz -o /tmp/vpn.tar.gz
sudo tar xzf /tmp/vpn.tar.gz -C /opt
sudo SKIP_DEPS=1 bash /opt/VPN_Android_Windows-main/server/install.sh
```

如果脚本提示“缺少命令”，按提示补装；如果提示内核不支持 WireGuard，说明第 3 步的模块没有装好。

之后管理命令：

```bash
sudo litetunnel list
sudo litetunnel add phone
sudo litetunnel status
```

## 5. 仍然失败怎么办？

CentOS 7 的软件源、内核、证书链都非常旧，继续排错的时间通常超过重装系统。
在 VPS 控制台重装 Ubuntu 22.04/24.04 或 Debian 12，再执行 README 的一行命令，是最稳妥的方案。
