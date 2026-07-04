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
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference

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
        if (uris.isEmpty()) {
            updateNotification("没有可发送的文件", 0, 0, false)
            stopForeground(false)
            stopSelf(startId)
            return
        }

        val progress = ConcurrentHashMap<Uri, FileProgress>()
        val okCount = AtomicInteger(0)
        val doneCount = AtomicInteger(0)
        val firstError = AtomicReference<String?>(null)

        val fileInfos = uris.map { uri ->
            val name = FileUtils.displayName(this, uri)
            val size = FileUtils.size(this, uri)
            UploadItem(uri, name, size)
        }

        val parallelism = chooseParallelism(fileInfos)
        val executor = Executors.newFixedThreadPool(parallelism)

        fileInfos.forEach { item ->
            progress[item.uri] = FileProgress(name = item.name, sent = 0L, total = item.size, done = false)
        }

        updateAggregateNotification(progress, doneCount.get(), uris.size)

        try {
            val futures = fileInfos.map { item ->
                executor.submit {
                    val uri = item.uri
                    val name = item.name
                    var uploaded = false
                    var lastError: Exception? = null

                    for (url in device.urls) {
                        try {
                            uploadOne(url, device.token, uri) { sent, total ->
                                progress[uri] = FileProgress(name, sent, total, done = false)
                                updateAggregateNotification(progress, doneCount.get(), uris.size)
                            }
                            uploaded = true
                            okCount.incrementAndGet()
                            progress[uri] = progress[uri]?.copy(done = true) ?: FileProgress(name, 0L, -1L, true)
                            doneCount.incrementAndGet()
                            updateAggregateNotification(progress, doneCount.get(), uris.size)
                            break
                        } catch (e: Exception) {
                            lastError = e
                        }
                    }

                    if (!uploaded) {
                        val message = "${name} 发送失败：${lastError?.message ?: "未知错误"}"
                        firstError.compareAndSet(null, message)
                        progress[uri] = progress[uri]?.copy(done = true) ?: FileProgress(name, 0L, -1L, true)
                        doneCount.incrementAndGet()
                        updateAggregateNotification(progress, doneCount.get(), uris.size)
                    }
                }
            }

            futures.forEach { it.get() }
        } catch (e: Exception) {
            firstError.compareAndSet(null, e.message ?: e.javaClass.simpleName)
        } finally {
            executor.shutdownNow()
        }

        val failMessage = firstError.get()
        if (failMessage == null) {
            updateNotification("发送完成：${okCount.get()} 个文件", 100, 100, false)
        } else {
            updateNotification("发送完成 ${okCount.get()}/${uris.size}，失败：$failMessage", 0, 0, false)
        }

        stopForeground(false)
        stopSelf(startId)
    }

    private fun updateAggregateNotification(progress: Map<Uri, FileProgress>, done: Int, totalFiles: Int) {
        val totals = progress.values
        val knownTotal = totals.filter { it.total > 0 }.sumOf { it.total }
        val knownSent = totals.filter { it.total > 0 }.sumOf { it.sent.coerceAtMost(it.total) }

        if (knownTotal > 0 && totals.all { it.total > 0 }) {
            val percent = ((knownSent * 100) / knownTotal).coerceIn(0, 100).toInt()
            updateNotification("正在发送 $done/$totalFiles 个文件：$percent%", percent, 100, false)
        } else {
            updateNotification("正在发送 $done/$totalFiles 个文件", done, totalFiles, true)
        }
    }

    private fun chooseParallelism(items: List<UploadItem>): Int {
        if (items.size <= 1) return 1

        val knownSizes = items.map { it.size }.filter { it > 0 }
        if (knownSizes.isEmpty()) return minOf(DEFAULT_PARALLEL_UPLOADS, items.size)

        val maxSize = knownSizes.maxOrNull() ?: 0L
        val totalSize = knownSizes.sum()
        val limit = when {
            maxSize >= VERY_LARGE_FILE_BYTES -> 2
            maxSize >= LARGE_FILE_BYTES -> 3
            totalSize >= LARGE_BATCH_BYTES -> 4
            else -> DEFAULT_PARALLEL_UPLOADS
        }

        return minOf(limit, items.size)
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
        private const val DEFAULT_PARALLEL_UPLOADS = 8
        private const val LARGE_FILE_BYTES = 200L * 1024L * 1024L
        private const val VERY_LARGE_FILE_BYTES = 1024L * 1024L * 1024L
        private const val LARGE_BATCH_BYTES = 1500L * 1024L * 1024L
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

private data class UploadItem(
    val uri: Uri,
    val name: String,
    val size: Long
)

private data class FileProgress(
    val name: String,
    val sent: Long,
    val total: Long,
    val done: Boolean
)

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
