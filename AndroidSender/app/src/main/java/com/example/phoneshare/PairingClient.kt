package com.example.phoneshare

import android.content.Context
import android.os.Build
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.util.concurrent.TimeUnit

data class PairingResult(
    val device: PairedDevice?,
    val error: String?
)

object PairingClient {
    private val client = OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .readTimeout(8, TimeUnit.SECONDS)
        .writeTimeout(8, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()

    fun register(context: Context, device: PairedDevice): PairingResult {
        var lastError: String? = null
        for (base in device.urls) {
            val cleanBase = base.trimEnd('/')
            try {
                val payload = JSONObject().apply {
                    put("phoneName", Build.MODEL ?: "Android")
                    put("manufacturer", Build.MANUFACTURER ?: "Android")
                    put("androidVersion", Build.VERSION.RELEASE ?: "")
                    put("deviceId", PhoneIdentity.getDeviceId(context))
                }.toString().toRequestBody("application/json; charset=utf-8".toMediaType())

                val request = Request.Builder()
                    .url("$cleanBase/pair")
                    .header("Authorization", "Bearer ${device.token}")
                    .post(payload)
                    .build()

                client.newCall(request).execute().use { response ->
                    if (response.isSuccessful) {
                        // 把当前可连通 URL 放到最前面，后续发送时优先使用它。
                        val orderedUrls = listOf(cleanBase) + device.urls.filterNot { it.trimEnd('/') == cleanBase }
                        return PairingResult(device.copy(urls = orderedUrls), null)
                    }
                    val body = response.body?.string()?.take(180).orEmpty()
                    lastError = "$cleanBase 返回 HTTP ${response.code}${if (body.isNotBlank()) ": $body" else ""}"
                }
            } catch (e: Exception) {
                lastError = "$cleanBase 连接失败：${e.message ?: e.javaClass.simpleName}"
            }
        }

        return PairingResult(
            null,
            "手机已读到二维码，但无法连接电脑接收端。最后一次错误：${lastError ?: "未知错误"}\n\n请检查：\n1. 手机和电脑是否在同一 Wi-Fi；\n2. Windows 防火墙是否允许专用网络；\n3. 电脑 VPN/TUN 是否允许局域网访问；\n4. 二维码中的 IP 是否是 192.168.x.x / 10.x.x.x / 172.16-31.x.x。"
        )
    }
}
