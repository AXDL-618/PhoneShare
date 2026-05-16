package com.example.phoneshare

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject

data class PairedDevice(
    val name: String,
    val deviceId: String,
    val token: String,
    val urls: List<String>
) {
    fun toJson(): JSONObject = JSONObject().apply {
        put("name", name)
        put("deviceId", deviceId)
        put("token", token)
        put("urls", JSONArray(urls))
    }

    companion object {
        fun fromJson(obj: JSONObject): PairedDevice {
            val arr = obj.optJSONArray("urls") ?: JSONArray()
            val urls = mutableListOf<String>()
            for (i in 0 until arr.length()) {
                val v = arr.optString(i).trim()
                if (v.isNotEmpty()) urls.add(v.trimEnd('/'))
            }
            return PairedDevice(
                name = obj.optString("name", "My PC"),
                deviceId = obj.optString("deviceId", ""),
                token = obj.optString("token", ""),
                urls = urls
            )
        }

        fun fromPairingQr(content: String): PairedDevice {
            val obj = JSONObject(content)
            val type = obj.optString("type")
            require(type == "PhoneSharePairing") { "不是 PhoneShare 配对二维码" }
            val urlsArray = obj.optJSONArray("urls") ?: JSONArray()
            val urls = mutableListOf<String>()
            for (i in 0 until urlsArray.length()) {
                val u = urlsArray.optString(i).trim().trimEnd('/')
                if (u.startsWith("http://") || u.startsWith("https://")) urls.add(u)
            }
            require(urls.isNotEmpty()) { "二维码中没有可用的电脑地址" }
            val token = obj.optString("token")
            require(token.isNotBlank()) { "二维码中没有 Token" }
            return PairedDevice(
                name = obj.optString("name", "My PC"),
                deviceId = obj.optString("deviceId", System.currentTimeMillis().toString()),
                token = token,
                urls = urls
            )
        }
    }
}

class DeviceStore(private val context: Context) {
    private val prefs = context.getSharedPreferences("devices", Context.MODE_PRIVATE)

    fun list(): List<PairedDevice> {
        val raw = prefs.getString("items", "[]") ?: "[]"
        val array = try { JSONArray(raw) } catch (_: Exception) { JSONArray() }
        val result = mutableListOf<PairedDevice>()
        for (i in 0 until array.length()) {
            val obj = array.optJSONObject(i) ?: continue
            runCatching { PairedDevice.fromJson(obj) }.getOrNull()?.let {
                if (it.token.isNotBlank() && it.urls.isNotEmpty()) result.add(it)
            }
        }
        return result
    }

    fun upsert(device: PairedDevice) {
        val current = list().filterNot { it.deviceId == device.deviceId }.toMutableList()
        current.add(0, device)
        save(current)
    }

    fun remove(deviceId: String) {
        save(list().filterNot { it.deviceId == deviceId })
    }

    fun clear() {
        save(emptyList())
    }

    private fun save(devices: List<PairedDevice>) {
        val array = JSONArray()
        devices.forEach { array.put(it.toJson()) }
        prefs.edit().putString("items", array.toString()).apply()
    }
}
