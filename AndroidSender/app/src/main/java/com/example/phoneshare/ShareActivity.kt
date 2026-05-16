package com.example.phoneshare

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.widget.Toast

class ShareActivity : Activity() {
    private lateinit var store: DeviceStore

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = DeviceStore(this)
        val uris = extractUris(intent)
        if (uris.isEmpty()) {
            toast("没有可发送的文件")
            finish()
            return
        }

        val devices = store.list()
        if (devices.isEmpty()) {
            AlertDialog.Builder(this)
                .setTitle("还没有绑定电脑")
                .setMessage("请先打开 PhoneShare，扫描电脑端二维码完成绑定。")
                .setPositiveButton("去绑定") { _, _ ->
                    startActivity(Intent(this, MainActivity::class.java))
                    finish()
                }
                .setNegativeButton("取消") { _, _ -> finish() }
                .setOnCancelListener { finish() }
                .show()
            return
        }

        if (devices.size == 1) {
            UploadService.start(this, ArrayList(uris), devices.first())
            toast("开始发送到 ${devices.first().name}")
            finish()
            return
        }

        AlertDialog.Builder(this)
            .setTitle("发送到哪台电脑？")
            .setItems(devices.map { it.name }.toTypedArray()) { _, which ->
                UploadService.start(this, ArrayList(uris), devices[which])
                toast("开始发送到 ${devices[which].name}")
                finish()
            }
            .setOnCancelListener { finish() }
            .show()
    }

    private fun extractUris(intent: Intent): List<Uri> {
        return when (intent.action) {
            Intent.ACTION_SEND -> {
                val uri = intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)
                if (uri != null) listOf(uri) else emptyList()
            }
            Intent.ACTION_SEND_MULTIPLE -> {
                intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)?.toList() ?: emptyList()
            }
            else -> emptyList()
        }
    }

    private fun toast(msg: String) = Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()
}
