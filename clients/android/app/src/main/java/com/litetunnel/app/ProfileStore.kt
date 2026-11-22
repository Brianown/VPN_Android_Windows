package com.litetunnel.app

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/**
 * 使用 SharedPreferences 保存节点列表。
 *
 * 注意：配置中包含 WireGuard 私钥，因此应用已关闭系统备份（allowBackup=false），
 * 私钥只保存在本机应用私有目录中。
 */
class ProfileStore(context: Context) {

    private val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    fun load(): List<Profile> {
        val raw = prefs.getString(KEY_PROFILES, null) ?: return emptyList()
        return try {
            val array = JSONArray(raw)
            buildList {
                for (i in 0 until array.length()) {
                    val obj = array.optJSONObject(i) ?: continue
                    val config = obj.optString("config")
                    if (config.isBlank()) continue
                    val id = obj.optString("id").ifBlank { UUID.randomUUID().toString() }
                    val name = obj.optString("name").ifBlank { "未命名节点" }
                    add(Profile(id, name, config))
                }
            }
        } catch (e: Exception) {
            emptyList()
        }
    }

    fun save(profiles: List<Profile>) {
        val array = JSONArray()
        profiles.forEach { profile ->
            array.put(JSONObject().apply {
                put("id", profile.id)
                put("name", profile.name)
                put("config", profile.config)
            })
        }
        prefs.edit().putString(KEY_PROFILES, array.toString()).apply()
    }

    companion object {
        private const val PREFS_NAME = "litetunnel"
        private const val KEY_PROFILES = "profiles_v1"
    }
}
