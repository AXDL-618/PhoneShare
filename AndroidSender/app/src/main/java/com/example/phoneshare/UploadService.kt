package com.example.phoneshare

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.Build
import android.os.IBinder
import android.widget.Toast
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okio.BufferedSink
import java.io.IOException
import java.util.concurrent.TimeUnit

class UploadService : Service() {
    private val client = OkHttpClient.Builder()
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(5, TimeUnit.MINUTES)
        .writeTimeout(30, TimeUnit.MINUTES)
        .callTimeout(0, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        createChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent == null) {
            stopSelf(startId)
            return START_NOT_STICKY
        }

        val uris = intent.getParcelableArrayListExtra<Uri>(EXTRA_URIS) ?: arrayListOf()
        val device = PairedDevice(
            name = intent.getStringExtra(EXTRA_DEVICE_NAME) ?: "My PC",
            deviceId = intent.getStringExtra(EXTRA_DEVICE_ID) ?: "",
            token = intent.getStringExtra(EXTRA_TOKEN) ?: "",
            urls = intent.getStringArrayListExtra(EXTRA_URLS)?.toList() ?: emptyList()
        )

        startForegroundCompat(buildNotification("准备发送", 0, 0, indeterminate = true))

        Thread {
            runUpload(startId, uris, device)
        }.start()

        return START_NOT_STICKY
    }

    private fun runUpload(startId: Int, uris: List<Uri>, device: PairedDevice) {
        var okCount = 0
        var failMessage: String? = null
        var preferredUrl: String? = null

        try {
            uris.forEachIndexed { index, uri ->
                val name = FileUtils.displayName(this, uri)
                updateNotification("正在发送 ${index + 1}/${uris.size}: $name", index, uris.size, true)

                val urlsToTry = buildList {
                    preferredUrl?.let { add(it) }
                    addAll(device.urls.filterNot { it == preferredUrl })
                }

                var uploaded = false
                var lastError: Exception? = null
                for (url in urlsToTry) {
                    try {
                        uploadOne(url, device.token, uri) { sent, total ->
                            if (total > 0) {
                                val percent = ((sent * 100) / total).coerceIn(0, 100).toInt()
                                updateNotification("正在发送 ${index + 1}/${uris.size}: $percent%", percent, 100, false)
                            }
                        }
                        preferredUrl = url
                        uploaded = true
                        okCount++
                        break
                    } catch (e: Exception) {
                        lastError = e
                    }
                }

                if (!uploaded) {
                    throw IOException("${name} 发送失败：${lastError?.message ?: "未知错误"}")
                }
            }
        } catch (e: Exception) {
            failMessage = e.message ?: e.javaClass.simpleName
        }

        if (failMessage == null) {
            updateNotification("发送完成：$okCount 个文件", 100, 100, false)
        } else {
            updateNotification("发送失败：$failMessage", 0, 0, false)
        }

        stopForeground(false)
        stopSelf(startId)
    }

    private fun uploadOne(baseUrl: String, token: String, uri: Uri, onProgress: (Long, Long) -> Unit) {
        val cleanBase = baseUrl.trimEnd('/')
        val fileName = FileUtils.displayName(this, uri)
        val mime = FileUtils.mimeType(this, uri)
        val body = UriRequestBody(this, uri, mime, onProgress)

        val multipart = MultipartBody.Builder()
            .setType(MultipartBody.FORM)
            .addFormDataPart("device", Build.MODEL ?: "Android")
            .addFormDataPart("files", fileName, body)
            .build()

        val request = Request.Builder()
            .url("$cleanBase/upload")
            .header("Authorization", "Bearer $token")
            .post(multipart)
            .build()

        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) {
                val text = response.body?.string()?.take(200) ?: ""
                throw IOException("HTTP ${response.code}: $text")
            }
        }
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT >= 26) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "PhoneShare 上传",
                NotificationManager.IMPORTANCE_LOW
            )
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
    }

    private fun buildNotification(title: String, progress: Int, max: Int, indeterminate: Boolean): Notification {
        val pendingIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(com.example.phoneshare.R.drawable.ic_notification)
            .setContentTitle("PhoneShare")
            .setContentText(title)
            .setOngoing(indeterminate || (max > 0 && progress < max))
            .setProgress(max, progress, indeterminate)
            .setContentIntent(pendingIntent)
            .build()
    }

    private fun updateNotification(title: String, progress: Int, max: Int, indeterminate: Boolean) {
        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(NOTIFICATION_ID, buildNotification(title, progress, max, indeterminate))
    }

    private fun startForegroundCompat(notification: Notification) {
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    companion object {
        private const val CHANNEL_ID = "phone_share_upload"
        private const val NOTIFICATION_ID = 23001
        private const val EXTRA_URIS = "uris"
        private const val EXTRA_DEVICE_NAME = "deviceName"
        private const val EXTRA_DEVICE_ID = "deviceId"
        private const val EXTRA_TOKEN = "token"
        private const val EXTRA_URLS = "urls"

        fun start(context: Context, uris: ArrayList<Uri>, device: PairedDevice) {
            val intent = Intent(context, UploadService::class.java).apply {
                putParcelableArrayListExtra(EXTRA_URIS, uris)
                putExtra(EXTRA_DEVICE_NAME, device.name)
                putExtra(EXTRA_DEVICE_ID, device.deviceId)
                putExtra(EXTRA_TOKEN, device.token)
                putStringArrayListExtra(EXTRA_URLS, ArrayList(device.urls))
            }
            ContextCompat.startForegroundService(context, intent)
        }
    }
}

class UriRequestBody(
    private val context: Context,
    private val uri: Uri,
    private val mimeType: String,
    private val onProgress: (Long, Long) -> Unit
) : RequestBody() {
    override fun contentType() = mimeType.toMediaTypeOrNull()

    override fun contentLength(): Long = FileUtils.size(context, uri)

    override fun writeTo(sink: BufferedSink) {
        val total = contentLength()
        var uploaded = 0L
        val buffer = ByteArray(DEFAULT_BUFFER_SIZE)

        context.contentResolver.openInputStream(uri).use { input ->
            if (input == null) throw IOException("无法读取文件")
            while (true) {
                val read = input.read(buffer)
                if (read == -1) break
                sink.write(buffer, 0, read)
                uploaded += read
                onProgress(uploaded, total)
            }
        }
    }

    companion object {
        private const val DEFAULT_BUFFER_SIZE = 128 * 1024
    }
}
