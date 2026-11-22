package com.litetunnel.app

import android.content.Context
import java.util.concurrent.atomic.AtomicReference

/**
 * 智能分流：把 WireGuard 配置里的 AllowedIPs 从 0.0.0.0/0 替换为“非中国 IPv4 段”，
 * 这样只有国外 IP 会进入 VPN，国内 IP 走本地直连。
 *
 * 这是基于 IP 段的分流，不是按域名分流：少数使用国内 CDN 的国外站点可能直连，
 * 少数使用国外 CDN 的国内站点可能走 VPN。列表由 APNIC delegation 数据生成。
 */
object SmartRoute {

    private const val ASSET_NAME = "non_china_ipv4.txt"
    private val cache = AtomicReference<String?>(null)
    private val allowedIpsRegex = Regex("(?m)^(\\s*AllowedIPs\\s*=\\s*).*$")
    private val statsHostRegex = Regex("(?m)^\\s*#\\s*StatsURL\\s*=\\s*https?://([^/:\\s]+)")

    fun build(context: Context, originalConfig: String): String {
        val routeList = loadRouteList(context)
        // 月度流量 API 位于 VPN 内网（例如 10.66.66.1），必须保留一条到它的路由，
        // 否则智能分流会把它当成“非公网地址”直连而访问不到。
        val statsHost = statsHostRegex.find(originalConfig)?.groupValues?.get(1)
        val extra = if (!statsHost.isNullOrBlank()) "$statsHost/32, " else ""
        val match = allowedIpsRegex.find(originalConfig) ?: return originalConfig
        return originalConfig.replaceRange(
            match.range.first,
            match.range.last + 1,
            match.groupValues[1] + extra + routeList
        )
    }

    private fun loadRouteList(context: Context): String {
        cache.get()?.let { return it }
        synchronized(this) {
            cache.get()?.let { return it }
            val text = context.assets.open(ASSET_NAME)
                .bufferedReader(Charsets.UTF_8)
                .use { it.readText() }
            val joined = text.lineSequence()
                .map { it.trim() }
                .filter { it.isNotEmpty() }
                .joinToString(", ")
            cache.set(joined)
            return joined
        }
    }
}
