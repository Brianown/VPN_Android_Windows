package com.litetunnel.app

import android.content.ClipData
import android.content.ClipboardManager
import android.content.DialogInterface
import android.content.res.ColorStateList
import android.net.Uri
import android.net.VpnService
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Menu
import android.view.MenuItem
import android.view.View
import android.widget.Toast
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.recyclerview.widget.LinearLayoutManager
import com.google.android.material.dialog.MaterialAlertDialogBuilder
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.litetunnel.app.databinding.ActivityMainBinding
import com.litetunnel.app.databinding.DialogEditProfileBinding
import com.wireguard.android.backend.Tunnel
import com.wireguard.config.Config
import java.io.ByteArrayInputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.UUID
import java.util.concurrent.Executors

class MainActivity : AppCompatActivity(), VpnController.Listener {

    private lateinit var binding: ActivityMainBinding
    private lateinit var store: ProfileStore
    private lateinit var adapter: ProfileAdapter

    private val profiles = mutableListOf<Profile>()
    private var pendingProfile: Profile? = null
    private var routeMode = RouteMode.GLOBAL

    private val statsHandler = Handler(Looper.getMainLooper())
    private val statsRunnable = object : Runnable {
        override fun run() {
            updateTraffic()
            statsHandler.postDelayed(this, 1000L)
        }
    }

    private val monthlyHandler = Handler(Looper.getMainLooper())
    private val monthlyExecutor = Executors.newSingleThreadExecutor()
    private val monthlyRunnable = object : Runnable {
        override fun run() {
            refreshMonthlyStats()
            monthlyHandler.postDelayed(this, 60_000L)
        }
    }

    private val latencyHandler = Handler(Looper.getMainLooper())
    private val latencyExecutor = Executors.newFixedThreadPool(4)
    private val latencyRunnable = object : Runnable {
        override fun run() {
            measureLatencies()
            latencyHandler.postDelayed(this, 30_000L)
        }
    }

    private val vpnPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        val profile = pendingProfile
        pendingProfile = null
        if (result.resultCode == RESULT_OK && profile != null) {
            VpnController.connect(this, profile, routeMode)
        } else {
            toast("未获得 VPN 授权，无法连接")
        }
    }

    private val scanLauncher = registerForActivityResult(ScanContract()) { result ->
        val contents = result.contents
        if (!contents.isNullOrBlank()) {
            showEditDialog(initialConfig = contents)
        }
    }

    private val openDocumentLauncher = registerForActivityResult(
        ActivityResultContracts.OpenDocument()
    ) { uri: Uri? ->
        if (uri == null) return@registerForActivityResult
        try {
            val text = contentResolver.openInputStream(uri)
                ?.bufferedReader(Charsets.UTF_8)
                ?.use { it.readText() }
            if (text.isNullOrBlank()) {
                toast("文件内容为空")
                return@registerForActivityResult
            }
            val fallbackName = uri.lastPathSegment
                ?.substringAfterLast('/')
                ?.substringBeforeLast('.')
                ?.takeIf { it.isNotBlank() }
            showEditDialog(initialConfig = text, initialName = fallbackName)
        } catch (e: Exception) {
            toast("读取文件失败：${e.message}")
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)

        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        ViewCompat.setOnApplyWindowInsetsListener(binding.root) { view, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }

        setSupportActionBar(binding.toolbar)

        store = ProfileStore(this)
        profiles.addAll(store.load())
        routeMode = RouteMode.from(
            getSharedPreferences("settings", MODE_PRIVATE)
                .getString("route_mode", null)
        )

        adapter = ProfileAdapter(
            onClick = { toggleConnection(it) },
            onLongClick = { showProfileMenu(it) }
        )
        binding.recyclerView.layoutManager = LinearLayoutManager(this)
        binding.recyclerView.adapter = adapter
        binding.fab.setOnClickListener { showEditDialog() }

        refreshList()
    }

    override fun onStart() {
        super.onStart()
        VpnController.addListener(this)
        if (VpnController.state != Tunnel.State.UP) {
            VpnController.restore(this)
        }
        if (VpnController.state == Tunnel.State.UP) {
            startMonthlyStats()
        }
        latencyHandler.removeCallbacks(latencyRunnable)
        latencyHandler.post(latencyRunnable)
    }

    override fun onStop() {
        VpnController.removeListener(this)
        statsHandler.removeCallbacks(statsRunnable)
        monthlyHandler.removeCallbacks(monthlyRunnable)
        latencyHandler.removeCallbacks(latencyRunnable)
        super.onStop()
    }

    override fun onCreateOptionsMenu(menu: Menu): Boolean {
        menuInflater.inflate(R.menu.menu_main, menu)
        return true
    }

    override fun onOptionsItemSelected(item: MenuItem): Boolean {
        if (item.itemId == R.id.action_about) {
            showAbout()
            return true
        }
        if (item.itemId == R.id.action_route_mode) {
            showRouteModeDialog()
            return true
        }
        return super.onOptionsItemSelected(item)
    }

    // ---------------- 连接控制 ----------------

    private fun toggleConnection(profile: Profile) {
        if (VpnController.state == Tunnel.State.UP && VpnController.activeProfileId == profile.id) {
            VpnController.disconnect(this)
            return
        }
        if (VpnController.state == Tunnel.State.UP) {
            // 同一时刻只保留一条隧道。
            VpnController.disconnect(this)
        }
        val intent = VpnService.prepare(this)
        if (intent != null) {
            pendingProfile = profile
            vpnPermissionLauncher.launch(intent)
        } else {
            VpnController.connect(this, profile, routeMode)
        }
    }

    override fun onStateChanged(state: Tunnel.State, profileId: String?) {
        refreshList()
        if (state == Tunnel.State.UP) {
            statsHandler.removeCallbacks(statsRunnable)
            statsHandler.post(statsRunnable)
            startMonthlyStats()
        } else {
            statsHandler.removeCallbacks(statsRunnable)
            binding.trafficText.text = ""
            stopMonthlyStats()
        }
    }

    override fun onError(message: String) {
        toast(message)
        refreshList()
    }

    // ---------------- 界面刷新 ----------------

    private fun refreshList() {
        val up = VpnController.state == Tunnel.State.UP
        val active = VpnController.activeProfileId
        val activeProfile = profiles.firstOrNull { it.id == active }

        binding.statusDot.backgroundTintList = ColorStateList.valueOf(
            getColor(if (up) R.color.success else R.color.muted)
        )
        binding.statusTitle.text = if (up) "已连接" else "未连接"
        binding.statusSubtitle.text = when {
            up && activeProfile != null -> "${activeProfile.name} · 已连接 · ${routeMode.label}"
            up -> "VPN 已连接 · ${routeMode.label}"
            else -> "当前模式：${routeMode.label} · 点击下方节点连接"
        }
        if (!up) binding.trafficText.text = ""

        val empty = profiles.isEmpty()
        binding.emptyView.visibility = if (empty) View.VISIBLE else View.GONE
        binding.recyclerView.visibility = if (empty) View.GONE else View.VISIBLE
        adapter.submit(profiles, active)
    }

    private fun updateTraffic() {
        if (VpnController.state != Tunnel.State.UP) return
        val (rx, tx) = VpnController.stats() ?: return
        val elapsed = if (VpnController.connectedAt > 0L) {
            formatDuration(System.currentTimeMillis() - VpnController.connectedAt)
        } else {
            ""
        }
        binding.trafficText.text = "$elapsed  ↓${formatBytes(rx)}  ↑${formatBytes(tx)}"
    }

    // ---------------- 服务端月度流量 ----------------

    private fun startMonthlyStats() {
        monthlyHandler.removeCallbacks(monthlyRunnable)
        binding.monthlyText.text = "本月流量：获取中…"
        monthlyHandler.postDelayed(monthlyRunnable, 1500L)
    }

    private fun stopMonthlyStats() {
        monthlyHandler.removeCallbacks(monthlyRunnable)
        binding.monthlyText.text = "本月流量：连接后获取"
    }

    private fun refreshMonthlyStats() {
        if (VpnController.state != Tunnel.State.UP) return
        val profile = profiles.firstOrNull { it.id == VpnController.activeProfileId } ?: return
        if (!TrafficStatsClient.hasStatsConfig(profile)) {
            binding.monthlyText.text = "本月流量：服务端未配置统计"
            return
        }
        monthlyExecutor.execute {
            val data = TrafficStatsClient.fetch(profile)
            runOnUiThread {
                if (isFinishing || isDestroyed) return@runOnUiThread
                if (VpnController.state != Tunnel.State.UP ||
                    VpnController.activeProfileId != profile.id
                ) {
                    return@runOnUiThread
                }
                if (data == null) {
                    if (binding.monthlyText.text == "本月流量：获取中…") {
                        binding.monthlyText.text = "本月流量：暂时无法获取"
                    }
                } else {
                    val period = if (data.period.isBlank()) "" else "（${data.period}）"
                    // 服务端 rx = 服务器收到（客户端上传），tx = 服务器发出（客户端下载）
                    binding.monthlyText.text =
                        "本月流量：↓${formatBytes(data.tx)}  ↑${formatBytes(data.rx)}$period"
                }
            }
        }
    }

    // ---------------- 服务器延迟 ----------------

    private fun measureLatencies() {
        val snapshot = profiles.toList()
        if (snapshot.isEmpty()) return
        for (profile in snapshot) {
            val host = serverHostOf(profile.config) ?: continue
            val port = probePortOf(profile.config) ?: continue
            latencyExecutor.execute {
                val latency = tcpLatency(host, port)
                runOnUiThread {
                    if (isFinishing || isDestroyed) return@runOnUiThread
                    adapter.updateLatency(profile.id, latency)
                }
            }
        }
    }

    private fun tcpLatency(host: String, port: Int): Long? {
        val started = System.nanoTime()
        return try {
            Socket().use { socket ->
                socket.connect(InetSocketAddress(host, port), 1500)
            }
            (System.nanoTime() - started) / 1_000_000
        } catch (e: Exception) {
            null
        }
    }

    // ---------------- 节点管理 ----------------

    private fun showProfileMenu(profile: Profile) {
        val items = arrayOf("复制配置", "编辑节点", "删除节点")
        MaterialAlertDialogBuilder(this)
            .setTitle(profile.name)
            .setItems(items) { _, which ->
                when (which) {
                    0 -> copyProfile(profile)
                    1 -> showEditDialog(profile = profile)
                    2 -> confirmDelete(profile)
                }
            }
            .show()
    }

    private fun copyProfile(profile: Profile) {
        try {
            val clipboard = getSystemService(ClipboardManager::class.java)
            clipboard.setPrimaryClip(ClipData.newPlainText("WireGuard 配置", profile.config))
            toast("配置已复制到剪贴板")
        } catch (e: Exception) {
            toast("复制失败：${e.message}")
        }
    }

    private fun confirmDelete(profile: Profile) {
        MaterialAlertDialogBuilder(this)
            .setTitle("删除节点")
            .setMessage("确定要删除「${profile.name}」吗？")
            .setPositiveButton("删除") { _, _ ->
                if (VpnController.activeProfileId == profile.id) {
                    VpnController.disconnect(this)
                }
                profiles.removeAll { it.id == profile.id }
                store.save(profiles)
                refreshList()
                toast("节点已删除")
            }
            .setNegativeButton("取消", null)
            .show()
    }

    private fun showEditDialog(
        profile: Profile? = null,
        initialConfig: String? = null,
        initialName: String? = null
    ) {
        val view = DialogEditProfileBinding.inflate(layoutInflater)
        view.nameInput.setText(profile?.name ?: initialName ?: suggestName(initialConfig))
        view.configInput.setText(profile?.config ?: initialConfig.orEmpty())
        view.configInput.setSelection(view.configInput.text?.length ?: 0)

        val dialog = MaterialAlertDialogBuilder(this)
            .setTitle(if (profile == null) "添加节点" else "编辑节点")
            .setView(view.root)
            .setPositiveButton("保存", null)
            .setNegativeButton("取消", null)
            .create()

        view.btnPaste.setOnClickListener {
            val text = readClipboard()
            if (text.isNullOrBlank()) {
                toast("剪贴板中没有文本")
            } else {
                view.configInput.setText(text)
                view.configInput.setSelection(view.configInput.text?.length ?: 0)
            }
        }

        view.btnScan.setOnClickListener {
            dialog.dismiss()
            scanLauncher.launch(
                ScanOptions()
                    .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                    .setPrompt("把服务端生成的二维码放入取景框")
                    .setBeepEnabled(false)
                    .setOrientationLocked(false)
            )
        }

        view.btnImport.setOnClickListener {
            dialog.dismiss()
            openDocumentLauncher.launch(arrayOf("*/*"))
        }

        dialog.setOnShowListener {
            dialog.getButton(DialogInterface.BUTTON_POSITIVE).setOnClickListener {
                val name = view.nameInput.text?.toString()?.trim().orEmpty()
                val configText = view.configInput.text?.toString()?.trim().orEmpty()

                if (name.isEmpty()) {
                    view.nameLayout.error = "请填写节点名称"
                    return@setOnClickListener
                }
                if (configText.isEmpty()) {
                    view.configLayout.error = "请粘贴或导入 WireGuard 配置"
                    return@setOnClickListener
                }
                if (!Regex("(?m)^\\s*Endpoint\\s*=").containsMatchIn(configText)) {
                    view.configLayout.error = "配置中缺少 Endpoint（服务器地址）"
                    return@setOnClickListener
                }
                try {
                    Config.parse(ByteArrayInputStream(configText.toByteArray(Charsets.UTF_8)))
                } catch (e: Exception) {
                    view.configLayout.error = "配置格式不正确：${e.message ?: "无法解析"}"
                    return@setOnClickListener
                }
                view.nameLayout.error = null
                view.configLayout.error = null

                if (profile == null) {
                    profiles.add(0, Profile(UUID.randomUUID().toString(), name, configText))
                } else {
                    val index = profiles.indexOfFirst { it.id == profile.id }
                    if (index >= 0) {
                        profiles[index] = profile.copy(name = name, config = configText)
                    }
                }
                store.save(profiles)
                refreshList()
                measureLatencies()
                dialog.dismiss()
                toast(if (profile == null) "节点已添加" else "节点已更新")
            }
        }

        dialog.show()
    }

    private fun readClipboard(): String? {
        return try {
            val clipboard = getSystemService(ClipboardManager::class.java)
            val clip = clipboard.primaryClip ?: return null
            if (clip.itemCount == 0) return null
            clip.getItemAt(0).coerceToText(this)?.toString()
        } catch (e: Exception) {
            null
        }
    }

    private fun suggestName(config: String?): String {
        if (config.isNullOrBlank()) return "我的节点"
        val marker = Regex("(?m)^\\s*#\\s*Name\\s*[:=]\\s*(.+)$").find(config)
        if (marker != null) return marker.groupValues[1].trim()
        return "我的节点"
    }

    // ---------------- 关于 ----------------

    private fun showRouteModeDialog() {
        val labels = arrayOf(
            "全局代理：全部流量走 VPN",
            "智能分流：国外走 VPN，国内直连"
        )
        val checked = if (routeMode == RouteMode.GLOBAL) 0 else 1
        MaterialAlertDialogBuilder(this)
            .setTitle("路由模式")
            .setSingleChoiceItems(labels, checked) { dialog, which ->
                val selected = if (which == 0) RouteMode.GLOBAL else RouteMode.SMART
                if (selected != routeMode) {
                    routeMode = selected
                    getSharedPreferences("settings", MODE_PRIVATE)
                        .edit()
                        .putString("route_mode", selected.key)
                        .apply()
                    val connected = VpnController.state == Tunnel.State.UP
                    if (connected) {
                        VpnController.disconnect(this)
                    }
                    refreshList()
                    toast(
                        if (connected) "已切换为${selected.label}，请重新连接"
                        else "已切换为${selected.label}"
                    )
                }
                dialog.dismiss()
            }
            .setNegativeButton("取消", null)
            .show()
    }

    private fun showAbout() {
        val version = try {
            packageManager.getPackageInfo(packageName, 0).versionName ?: "1.1.0"
        } catch (e: Exception) {
            "1.1.0"
        }
        MaterialAlertDialogBuilder(this)
            .setTitle("关于轻隧")
            .setMessage(
                "轻隧 LiteTunnel v$version\n\n" +
                    "基于 WireGuard 的开源 VPN 客户端，用于连接你自建的服务器。\n\n" +
                    "服务端一键安装、配置生成和常见问题请查看项目 README。\n" +
                    "本项目以 Apache-2.0 协议开源。"
            )
            .setPositiveButton("知道了", null)
            .show()
    }

    private fun toast(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
    }
}
