package com.example.phoneshare

import android.Manifest
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import com.google.zxing.integration.android.IntentIntegrator
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions

class MainActivity : ComponentActivity() {
    private lateinit var store: DeviceStore
    private lateinit var deviceList: LinearLayout
    private lateinit var emptyCard: LinearLayout
    private lateinit var statusText: TextView
    private var pendingPickDevice: PairedDevice? = null

    private val scanLauncher = registerForActivityResult(ScanContract()) { result ->
        val content = result.contents
        if (content.isNullOrBlank()) return@registerForActivityResult
        try {
            val device = PairedDevice.fromPairingQr(content)
            bindDevice(device)
        } catch (e: Exception) {
            showError("配对失败", e.message ?: "二维码内容不正确")
        }
    }

    private val cameraPermissionLauncher = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) startScan() else toast("需要相机权限才能扫码")
    }

    private val notificationPermissionLauncher = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    private val pickFilesLauncher = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (uris.isNullOrEmpty()) return@registerForActivityResult
        val target = pendingPickDevice
        pendingPickDevice = null
        if (target != null) {
            UploadService.start(this, ArrayList(uris), target)
            return@registerForActivityResult
        }
        val devices = store.list()
        if (devices.isEmpty()) {
            showError("还没有绑定电脑", "请先点击“扫码绑定电脑”。")
            return@registerForActivityResult
        }
        chooseDeviceAndUpload(ArrayList(uris))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = DeviceStore(this)
        requestNotificationPermissionIfNeeded()
        buildUi()
        renderDevices()
    }

    private fun buildUi() {
        window.statusBarColor = c("#F6F8FB")
        if (Build.VERSION.SDK_INT >= 23) {
            window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR
        }

        val scroll = ScrollView(this).apply {
            setBackgroundColor(c("#F6F8FB"))
            isFillViewport = true
        }
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(18), dp(18), dp(22))
        }
        scroll.addView(root)

        val header = card().apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(18), dp(18), dp(18))
        }
        root.addView(header, lpMatchWrap(bottom = 14))

        val topRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        header.addView(topRow)

        topRow.addView(logoView(), LinearLayout.LayoutParams(dp(42), dp(42)))

        val titleBox = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(12), 0, 0, 0)
        }
        topRow.addView(titleBox, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))

        titleBox.addView(TextView(this).apply {
            text = "PhoneShare"
            textSize = 26f
            setTextColor(c("#15181D"))
            setTypeface(typeface, Typeface.BOLD)
        })
        titleBox.addView(TextView(this).apply {
            text = "手机文件一键发送到 Windows 指定文件夹"
            textSize = 13.5f
            setTextColor(c("#6B7280"))
            setPadding(0, dp(3), 0, 0)
        })

        val desc = TextView(this).apply {
            text = "扫码绑定电脑后，可从相册、文件管理器或系统分享菜单直接发送文件。"
            textSize = 14f
            setLineSpacing(dp(2).toFloat(), 1.0f)
            setTextColor(c("#4B5563"))
            setPadding(0, dp(16), 0, 0)
        }
        header.addView(desc)

        val actions = card().apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(16), dp(16), dp(16))
        }
        root.addView(actions, lpMatchWrap(bottom = 14))

        actions.addView(primaryButton("扫码绑定电脑") { ensureCameraAndScan() }, lpMatchWrap(bottom = 10))
        actions.addView(secondaryButton("选择文件测试发送") { pickFilesLauncher.launch(arrayOf("*/*")) }, lpMatchWrap(bottom = 10))
        actions.addView(secondaryButton("打开应用设置") {
            startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                data = Uri.parse("package:$packageName")
            })
        }, lpMatchWrap())

        val devicesCard = card().apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(16), dp(16), dp(16))
        }
        root.addView(devicesCard, lpMatchWrap(bottom = 14))

        val deviceHeader = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        devicesCard.addView(deviceHeader)
        deviceHeader.addView(TextView(this).apply {
            text = "已绑定电脑"
            textSize = 18f
            setTextColor(c("#111827"))
            setTypeface(typeface, Typeface.BOLD)
        }, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))

        statusText = TextView(this).apply {
            textSize = 13f
            setTextColor(c("#10B981"))
        }
        deviceHeader.addView(statusText)

        emptyCard = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(14), dp(14), dp(14), dp(14))
            background = rounded(c("#F9FAFB"), dp(14), c("#E5E7EB"))
        }
        emptyCard.addView(TextView(this).apply {
            text = "暂无设备"
            textSize = 15f
            setTextColor(c("#374151"))
            setTypeface(typeface, Typeface.BOLD)
        })
        emptyCard.addView(TextView(this).apply {
            text = "请先在电脑端打开 PhoneShare Receiver，然后扫描左侧二维码。"
            textSize = 13f
            setTextColor(c("#6B7280"))
            setPadding(0, dp(5), 0, 0)
        })
        devicesCard.addView(emptyCard, lpMatchWrap(top = 14))

        deviceList = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        devicesCard.addView(deviceList, lpMatchWrap(top = 14))

        val tips = card(bg = c("#FFF7ED"), stroke = c("#FED7AA")).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(14), dp(16), dp(14))
        }
        root.addView(tips, lpMatchWrap())
        tips.addView(TextView(this).apply {
            text = "连接提示"
            textSize = 15f
            setTextColor(c("#9A3412"))
            setTypeface(typeface, Typeface.BOLD)
        })
        tips.addView(TextView(this).apply {
            text = "手机和电脑需要在同一局域网；如果电脑开启 VPN/TUN，请允许局域网访问。二维码里的地址应优先为 192.168.x.x、10.x.x.x 或 172.16–31.x.x。"
            textSize = 13f
            setTextColor(c("#9A3412"))
            setLineSpacing(dp(2).toFloat(), 1.0f)
            setPadding(0, dp(6), 0, 0)
        })

        setContentView(scroll)
    }

    private fun renderDevices() {
        val devices = store.list()
        deviceList.removeAllViews()
        emptyCard.visibility = if (devices.isEmpty()) View.VISIBLE else View.GONE
        statusText.text = if (devices.isEmpty()) "未绑定" else "${devices.size} 台"
        statusText.setTextColor(if (devices.isEmpty()) c("#9CA3AF") else c("#10B981"))

        devices.forEach { device ->
            val box = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(14), dp(14), dp(14), dp(14))
                background = rounded(c("#F9FAFB"), dp(14), c("#E5E7EB"))
            }

            val rowTitle = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            box.addView(rowTitle)
            rowTitle.addView(TextView(this).apply {
                text = device.name
                textSize = 16f
                setTextColor(c("#111827"))
                setTypeface(typeface, Typeface.BOLD)
            }, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))

            rowTitle.addView(TextView(this).apply {
                text = "已绑定"
                textSize = 12f
                setTextColor(c("#059669"))
                setPadding(dp(9), dp(4), dp(9), dp(4))
                background = rounded(c("#D1FAE5"), dp(999))
            })

            box.addView(TextView(this).apply {
                text = device.urls.joinToString("\n")
                textSize = 12.5f
                setTextColor(c("#6B7280"))
                setPadding(0, dp(6), 0, dp(10))
            })

            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            row.addView(primaryButton("选择文件发送") { pickFilesForDevice(device) }, LinearLayout.LayoutParams(0, dp(44), 1f).apply { setMargins(0, 0, dp(10), 0) })
            row.addView(secondaryButton("删除") {
                AlertDialog.Builder(this@MainActivity)
                    .setTitle("删除设备")
                    .setMessage("确定删除 ${device.name} 吗？删除后需要重新扫码绑定。")
                    .setPositiveButton("删除") { _, _ ->
                        store.remove(device.deviceId)
                        renderDevices()
                    }
                    .setNegativeButton("取消", null)
                    .show()
            }, LinearLayout.LayoutParams(dp(86), dp(44)))

            box.addView(row)
            deviceList.addView(box, lpMatchWrap(bottom = 12))
        }
    }


    private fun bindDevice(device: PairedDevice) {
        toast("正在连接电脑...")
        Thread {
            val result = PairingClient.register(this, device)
            runOnUiThread {
                if (result.device != null) {
                    store.upsert(result.device)
                    renderDevices()
                    toast("已绑定：${result.device.name}")
                } else {
                    showError(
                        "配对失败",
                        result.error ?: "手机无法连接电脑。请确认手机和电脑在同一 Wi-Fi，电脑防火墙允许 PhoneShare，VPN/TUN 允许局域网访问。"
                    )
                }
            }
        }.start()
    }

    private fun pickFilesForDevice(device: PairedDevice) {
        pendingPickDevice = device
        pickFilesLauncher.launch(arrayOf("*/*"))
    }

    private fun chooseDeviceAndUpload(uris: ArrayList<Uri>) {
        val devices = store.list()
        if (devices.size == 1) {
            UploadService.start(this, uris, devices.first())
            return
        }
        AlertDialog.Builder(this)
            .setTitle("选择目标电脑")
            .setItems(devices.map { it.name }.toTypedArray()) { _, which ->
                UploadService.start(this, uris, devices[which])
            }
            .show()
    }

    private fun ensureCameraAndScan() {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            startScan()
        } else {
            cameraPermissionLauncher.launch(Manifest.permission.CAMERA)
        }
    }

    private fun startScan() {
        val options = ScanOptions().apply {
            setDesiredBarcodeFormats(IntentIntegrator.QR_CODE)
            setPrompt("扫描电脑端 PhoneShare 二维码")
            setBeepEnabled(false)
            setOrientationLocked(true)
            setCaptureActivity(PortraitCaptureActivity::class.java)
        }
        scanLauncher.launch(options)
    }

    private fun requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    private fun showError(title: String, message: String) {
        AlertDialog.Builder(this)
            .setTitle(title)
            .setMessage(message)
            .setPositiveButton("知道了", null)
            .show()
    }

    private fun logoView(): View {
        val frame = FrameLayout(this).apply {
            background = rounded(c("#EEF2F7"), dp(12), c("#E5E7EB"))
            setPadding(dp(8), dp(8), dp(8), dp(8))
        }
        val icon = ImageView(this).apply {
            setImageResource(R.mipmap.ic_launcher)
            scaleType = ImageView.ScaleType.CENTER_INSIDE
        }
        frame.addView(icon, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
        return frame
    }

    private fun primaryButton(label: String, onClick: () -> Unit): Button = Button(this).apply {
        text = label
        textSize = 14f
        setTextColor(Color.WHITE)
        setAllCaps(false)
        setTypeface(typeface, Typeface.BOLD)
        background = rounded(c("#2F6FED"), dp(12))
        minHeight = dp(46)
        setPadding(dp(12), 0, dp(12), 0)
        setOnClickListener { onClick() }
    }

    private fun secondaryButton(label: String, onClick: () -> Unit): Button = Button(this).apply {
        text = label
        textSize = 14f
        setTextColor(c("#374151"))
        setAllCaps(false)
        background = rounded(c("#F3F4F6"), dp(12), c("#E5E7EB"))
        minHeight = dp(46)
        setPadding(dp(12), 0, dp(12), 0)
        setOnClickListener { onClick() }
    }

    private fun card(bg: Int = Color.WHITE, stroke: Int = c("#E5E7EB")): LinearLayout = LinearLayout(this).apply {
        background = rounded(bg, dp(16), stroke)
        elevation = dp(1).toFloat()
    }

    private fun rounded(color: Int, radius: Int, stroke: Int? = null): GradientDrawable = GradientDrawable().apply {
        shape = GradientDrawable.RECTANGLE
        setColor(color)
        cornerRadius = radius.toFloat()
        stroke?.let { setStroke(dp(1), it) }
    }

    private fun lpMatchWrap(top: Int = 0, bottom: Int = 0): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply {
            setMargins(0, top, 0, bottom)
        }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density + 0.5f).toInt()
    private fun c(hex: String): Int = Color.parseColor(hex)
    private fun toast(msg: String) = Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()
}
