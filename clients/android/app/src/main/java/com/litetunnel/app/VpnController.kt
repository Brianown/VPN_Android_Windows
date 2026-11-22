package com.litetunnel.app

import android.content.Context
import android.os.Handler
import android.os.Looper
import com.wireguard.android.backend.GoBackend
import com.wireguard.android.backend.Tunnel
import com.wireguard.config.Config
import java.io.ByteArrayInputStream
import java.util.concurrent.CopyOnWriteArraySet
import java.util.concurrent.Executors

/**
 * 全局 VPN 控制器：持有 GoBackend 和当前隧道，保证 Activity 重建后状态不丢失。
 */
object VpnController {

    private const val STATE_PREFS = "vpn_runtime_state"
    private const val KEY_ACTIVE_ID = "active_id"
    private const val KEY_ACTIVE_NAME = "active_name"
    private const val KEY_CONNECTED_AT = "connected_at"

    interface Listener {
        fun onStateChanged(state: Tunnel.State, profileId: String?)
        fun onError(message: String) {}
    }

    private val mainHandler = Handler(Looper.getMainLooper())
    private val executor = Executors.newSingleThreadExecutor()
    private val listeners = CopyOnWriteArraySet<Listener>()

    @Volatile
    private var appContextRef: Context? = null

    @Volatile
    private var backend: GoBackend? = null

    @Volatile
    private var currentTunnel: ProfileTunnel? = null

    @Volatile
    var state: Tunnel.State = Tunnel.State.DOWN
        private set

    @Volatile
    var activeProfileId: String? = null
        private set

    @Volatile
    var connectedAt: Long = 0L
        private set

    fun addListener(listener: Listener) {
        listeners.add(listener)
        mainHandler.post { listener.onStateChanged(state, activeProfileId) }
    }

    fun removeListener(listener: Listener) {
        listeners.remove(listener)
    }

    /**
     * 连接指定节点。调用前必须已经通过 [android.net.VpnService.prepare] 获得 VPN 授权。
     * routeMode 为 SMART 时，只把非中国 IPv4 路由进 VPN。
     */
    fun connect(context: Context, profile: Profile, routeMode: RouteMode = RouteMode.GLOBAL) {
        if (state == Tunnel.State.UP && activeProfileId == profile.id) return
        val appContext = context.applicationContext
        appContextRef = appContext
        executor.execute {
            try {
                val configText = if (routeMode == RouteMode.SMART) {
                    SmartRoute.build(appContext, profile.config)
                } else {
                    profile.config
                }
                val config = Config.parse(
                    ByteArrayInputStream(configText.toByteArray(Charsets.UTF_8))
                )
                val tunnel = ProfileTunnel(sanitizeTunnelName(profile.name)) { newState ->
                    if (newState == Tunnel.State.DOWN) {
                        connectedAt = 0L
                        updateState(Tunnel.State.DOWN, null)
                    }
                }
                val goBackend = backend ?: GoBackend(appContext).also { backend = it }
                val newState = goBackend.setState(tunnel, Tunnel.State.UP, config)
                currentTunnel = tunnel
                connectedAt = System.currentTimeMillis()
                updateState(newState, profile.id)
                if (newState == Tunnel.State.UP) {
                    saveActive(appContext, profile.id, tunnel.getName(), connectedAt)
                }
            } catch (e: Exception) {
                val message = e.message ?: e.javaClass.simpleName
                mainHandler.post {
                    listeners.forEach { it.onError("连接失败：$message") }
                }
            }
        }
    }

    /**
     * App 进程被系统杀掉后，VpnService 可能仍在运行。重新启动时从 GoBackend
     * 查询真实状态，避免界面错误显示“未连接”，也避免无法断开旧隧道。
     */
    fun restore(context: Context) {
        if (state == Tunnel.State.UP) return
        val appContext = context.applicationContext
        appContextRef = appContext
        executor.execute {
            val prefs = appContext.getSharedPreferences(STATE_PREFS, Context.MODE_PRIVATE)
            val id = prefs.getString(KEY_ACTIVE_ID, null)
            val name = prefs.getString(KEY_ACTIVE_NAME, null)
            val savedAt = prefs.getLong(KEY_CONNECTED_AT, 0L)
            if (id.isNullOrBlank() || name.isNullOrBlank()) return@execute
            try {
                val tunnel = ProfileTunnel(name) { newState ->
                    if (newState == Tunnel.State.DOWN) {
                        connectedAt = 0L
                        updateState(Tunnel.State.DOWN, null)
                    }
                }
                val goBackend = backend ?: GoBackend(appContext).also { backend = it }
                val actual = goBackend.getState(tunnel)
                if (actual == Tunnel.State.UP) {
                    currentTunnel = tunnel
                    connectedAt = savedAt
                    updateState(Tunnel.State.UP, id)
                } else {
                    clearActive(appContext)
                    updateState(Tunnel.State.DOWN, null)
                }
            } catch (e: Exception) {
                clearActive(appContext)
                updateState(Tunnel.State.DOWN, null)
            }
        }
    }

    fun disconnect(context: Context) {
        val tunnel = currentTunnel
        if (tunnel == null) {
            clearActive(context.applicationContext)
            updateState(Tunnel.State.DOWN, null)
            return
        }
        executor.execute {
            try {
                backend?.setState(tunnel, Tunnel.State.DOWN, null)
            } catch (e: Exception) {
                val message = e.message ?: e.javaClass.simpleName
                mainHandler.post {
                    listeners.forEach { it.onError("断开失败：$message") }
                }
            } finally {
                currentTunnel = null
                connectedAt = 0L
                clearActive(context.applicationContext)
                updateState(Tunnel.State.DOWN, null)
            }
        }
    }

    /** 返回 (接收字节数, 发送字节数)。仅连接状态下调用有效。 */
    fun stats(): Pair<Long, Long>? {
        val tunnel = currentTunnel ?: return null
        val goBackend = backend ?: return null
        return try {
            val statistics = goBackend.getStatistics(tunnel)
            statistics.totalRx() to statistics.totalTx()
        } catch (e: Exception) {
            null
        }
    }

    private fun saveActive(context: Context, profileId: String, tunnelName: String, at: Long) {
        context.getSharedPreferences(STATE_PREFS, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY_ACTIVE_ID, profileId)
            .putString(KEY_ACTIVE_NAME, tunnelName)
            .putLong(KEY_CONNECTED_AT, at)
            .apply()
    }

    private fun clearActive(context: Context?) {
        context ?: return
        context.getSharedPreferences(STATE_PREFS, Context.MODE_PRIVATE)
            .edit()
            .remove(KEY_ACTIVE_ID)
            .remove(KEY_ACTIVE_NAME)
            .remove(KEY_CONNECTED_AT)
            .apply()
    }

    private fun updateState(newState: Tunnel.State, profileId: String?) {
        state = newState
        activeProfileId = profileId
        if (newState != Tunnel.State.UP) {
            connectedAt = 0L
            clearActive(appContextRef)
        }
        mainHandler.post {
            listeners.forEach { it.onStateChanged(newState, profileId) }
        }
    }

    private class ProfileTunnel(
        private val name: String,
        private val callback: (Tunnel.State) -> Unit
    ) : Tunnel {
        override fun getName(): String = name

        override fun onStateChange(newState: Tunnel.State) {
            callback(newState)
        }
    }
}
