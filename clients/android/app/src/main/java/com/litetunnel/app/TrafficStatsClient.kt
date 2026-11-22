package com.litetunnel.app

import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

data class MonthlyTraffic(
    val rx: Long,
    val tx: Long,
    val period: String,
    val resetAt: String?
)

/**
 * 从服务端的 VPN 内网流量 API 读取当前客户端本自然月的累计流量。
 *
 * 配置注释由服务端生成：
 *   # StatsURL = http://10.66.66.1:8080/api/traffic
 *   # StatsToken = <随机 token>
 */
object TrafficStatsClient {

    private val urlRegex = Regex("(?m)^\\s*#\\s*StatsURL\\s*=\\s*(\\S+)")
    private val tokenRegex = Regex("(?m)^\\s*#\\s*StatsToken\\s*=\\s*(\\S+)")

    fun hasStatsConfig(profile: Profile): Boolean =
        urlRegex.containsMatchIn(profile.config) && tokenRegex.containsMatchIn(profile.config)

    fun fetch(profile: Profile): MonthlyTraffic? {
        val url = urlRegex.find(profile.config)?.groupValues?.get(1) ?: return null
        val token = tokenRegex.find(profile.config)?.groupValues?.get(1) ?: return null
        var connection: HttpURLConnection? = null
        return try {
            val separator = if (url.contains("?")) "&" else "?"
            val target = URL(url + separator + "token=" + URLEncoder.encode(token, "UTF-8"))
            connection = target.openConnection() as HttpURLConnection
            connection.requestMethod = "GET"
            connection.connectTimeout = 3000
            connection.readTimeout = 3000
            connection.useCaches = false
            connection.instanceFollowRedirects = false
            connection.setRequestProperty("Accept", "application/json")
            if (connection.responseCode != HttpURLConnection.HTTP_OK) {
                return null
            }
            val body = connection.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }
            val json = JSONObject(body)
            MonthlyTraffic(
                rx = json.optLong("rx", 0L),
                tx = json.optLong("tx", 0L),
                period = json.optString("period", ""),
                resetAt = json.optString("reset_at", "").takeIf { it.isNotEmpty() }
            )
        } catch (e: Exception) {
            null
        } finally {
            connection?.disconnect()
        }
    }
}
