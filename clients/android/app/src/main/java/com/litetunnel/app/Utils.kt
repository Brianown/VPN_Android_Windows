package com.litetunnel.app

import java.util.Locale

private val ENDPOINT_REGEX = Regex("(?m)^\\s*Endpoint\\s*=\\s*(\\S+)")

/** 从配置中提取 Endpoint，用于列表展示。 */
fun endpointOf(config: String): String =
    ENDPOINT_REGEX.find(config)?.groupValues?.get(1) ?: "未配置服务器"

/**
 * WireGuard 隧道名只允许 [a-zA-Z0-9_=+.-]，且最长 15 个字符。
 * 展示名称可以是中文，真正传给后端的名称需要清洗。
 */
fun sanitizeTunnelName(name: String): String {
    val cleaned = name
        .replace(Regex("[^A-Za-z0-9_=+.-]"), "_")
        .trim('.', '-', '_', '=', '+')
    return when {
        cleaned.isEmpty() -> "litetunnel"
        cleaned.length > 15 -> cleaned.take(15)
        else -> cleaned
    }
}

fun formatBytes(bytes: Long): String {
    if (bytes < 1024) return "$bytes B"
    val units = arrayOf("KB", "MB", "GB", "TB")
    var value = bytes.toDouble()
    var index = -1
    while (value >= 1024.0 && index < units.lastIndex) {
        value /= 1024.0
        index++
    }
    return String.format(Locale.US, "%.1f %s", value, units[index])
}

fun formatDuration(millis: Long): String {
    val totalSeconds = (millis / 1000).coerceAtLeast(0)
    val hours = totalSeconds / 3600
    val minutes = (totalSeconds % 3600) / 60
    val seconds = totalSeconds % 60
    return String.format(Locale.US, "%02d:%02d:%02d", hours, minutes, seconds)
}
