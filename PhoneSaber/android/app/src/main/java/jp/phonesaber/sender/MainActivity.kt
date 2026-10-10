package jp.phonesaber.sender

import android.Manifest
import android.content.pm.PackageManager
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.BatteryManager
import android.os.PowerManager
import android.util.Log
import android.widget.Switch
import android.net.Network
import android.net.wifi.WifiManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.TextView
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import java.util.Locale

class MainActivity : ComponentActivity() {
    private val main = Handler(Looper.getMainLooper())
    private lateinit var sender: LatestUdpSender
    private val lowLatencyWifi by lazy {
        applicationContext.getSystemService(WifiManager::class.java)
            .createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "PhoneSaber:sending")
            .apply { setReferenceCounted(false) }
    }
    private lateinit var camera: CameraSession
    private lateinit var discovery: PcDiscovery
    private lateinit var pcStatus: TextView
    private lateinit var health: TextView
    private lateinit var power: PowerManager
    private lateinit var battery: BatteryManager
    private var thermalStatus = -1
    private var developerNetworkOverride = false
    private var manualDestination = false
    private var sendingNetwork: Network? = null
    private val thermalListener = PowerManager.OnThermalStatusChangedListener { status ->
        if (foreground) {
            if (thermalStatus != status) Log.i("DeviceHealth", "発熱: ${ThermalLevel.fromStatus(thermalStatus).title} → ${ThermalLevel.fromStatus(status).title}")
            thermalStatus = status
            updateHealth()
        }
    }
    private lateinit var detection: TextView
    private lateinit var startStop: Button
    private lateinit var brightnessSlider: SeekBar
    private lateinit var dominanceSlider: SeekBar
    private lateinit var mirrorXSwitch: Switch
    private lateinit var mirrorYSwitch: Switch
    private lateinit var measurementSwitch: Switch
    private lateinit var fps60Switch: Switch
    private var mirrors = MirrorSettings()
    private var destination: Destination? = null
    private var wifi: Network? = null
    private var sending = false
    private lateinit var resumePolicy: SendingResumePolicy
    private val prefs by lazy { getSharedPreferences("destination", MODE_PRIVATE) }
    private val cameraBackoff = RecoveryBackoff()
    private var cameraStarted = false
    private var cameraStartedAt = 0.0
    private var cameraRetry: Runnable? = null
    private var cameraRecoveryMessage = ""
    private var automaticResumePending = false
    private var automaticResumeMessage = ""
    private var permissionPending = false
    private lateinit var recoveryStatus: TextView
    private var foreground = false
    private var brightness = 145
    private var dominance = 25
    private var lastStats = 0L
    private val permission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        permissionPending = false
        if (granted && foreground && resumePolicy.wantsSending) startSending()
        else if (!granted) toast("カメラの許可が必要です。設定から許可してください")
    }
    private val refresh = object : Runnable {
        override fun run() {
            if (!foreground) return
            if (sending && developerNetworkOverride) {
                val connectivity = getSystemService(ConnectivityManager::class.java)
                if (connectivity.activeNetwork != sendingNetwork) updateSendingDestination()
            }
            if (sending && cameraStarted) {
                val now = System.nanoTime() / 1e9
                val lastFrame = camera.lastFrameAt
                if (lastFrame > 0) {
                    cameraBackoff.receivedFrame(lastFrame)
                    cameraRecoveryMessage = ""
                    if (automaticResumePending) {
                        automaticResumeMessage = "自動で送信を再開しました"
                        automaticResumePending = false
                    }
                }
                if ((lastFrame > 0 && now - lastFrame >= 3) ||
                    (lastFrame == 0.0 && now - cameraStartedAt >= 10)) {
                    recoverCamera("カメラの映像が届いていません")
                }
            }
            updateRecoveryStatus()
            updateHealth()
            val now = System.nanoTime()
            val elapsed = (now - lastStats) / 1e9
            val red = sender.redSent.getAndSet(0) / elapsed
            val blue = sender.blueSent.getAndSet(0) / elapsed
            lastStats = now
            val status = camera.status
            detection.text = if (sending) String.format(Locale.JAPAN,
                "赤: %s / 送信 %.1f fps\n青: %s / 送信 %.1f fps\n%s\n%s",
                status.red, red, status.blue, blue, status.dimensions, sender.error ?: "")
                else "停止中\n赤: 未検出 / 送信 0 fps\n青: 未検出 / 送信 0 fps"
            main.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        savedInstanceState?.let { state ->
            val restored = ThresholdSettings.restore { key, default -> state.getInt(key, default) }
            brightness = restored.brightness; dominance = restored.dominance
        }
        sender = LatestUdpSender()
        resumePolicy = SendingResumePolicy(prefs.getBoolean(SendingResumePolicy.WAS_SENDING_KEY, false))
        power = getSystemService(PowerManager::class.java)
        battery = getSystemService(BatteryManager::class.java)
        val density = resources.displayMetrics.density
        fun dp(value: Int) = (value * density).toInt()
        val panel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(12), dp(16), dp(12))
        }
        val scroll = ScrollView(this).apply { addView(panel) }
        setContentView(scroll)
        ViewCompat.setOnApplyWindowInsetsListener(scroll) { view, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }
        fun label(text: String) = TextView(this).apply {
            this.text = text; textSize = 17f
            panel.addView(this, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT))
        }
        label("PhoneSaber — PCへ送信").apply { textSize = 23f; gravity = Gravity.CENTER_HORIZONTAL }
        val preview = PreviewView(this).apply { scaleType = PreviewView.ScaleType.FIT_CENTER }
        panel.addView(preview, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(250)))
        health = label("端末状態を確認中")
        pcStatus = label("PCを探索中")
        label("台（PC と同じ台を指定）")
        val stations = listOf("", "A", "B")
        val stationGroup = android.widget.RadioGroup(this).apply {
            orientation = LinearLayout.HORIZONTAL
            stations.forEach { station ->
                addView(android.widget.RadioButton(this@MainActivity).apply {
                    id = View.generateViewId()
                    tag = station
                    text = if (station.isEmpty()) "指定なし" else station
                    isChecked = station == (prefs.getString("station", "") ?: "")
                })
            }
            panel.addView(this)
        }
        val address = EditText(this).apply {
            hint = "PCのIPv4（例: 192.168.1.10）"
            inputType = InputType.TYPE_CLASS_PHONE
            setSingleLine(true)
            setText(prefs.getString("ip", ""))
            panel.addView(this)
        }
        Button(this).apply {
            text = "手入力を保存（空欄で自動探索）"
            panel.addView(this)
            setOnClickListener {
                val text = address.text.toString().trim()
                val parsed = ManualAddress.parse(text)
                if (text.isNotEmpty() && parsed == null) toast("PCのIPv4アドレスを入力してください")
                else {
                    prefs.edit().putString("ip", text).apply()
                    manualDestination = parsed != null
                    discovery.setManual(parsed)
                }
            }
        }
        startStop = Button(this).apply {
            text = "開始"; panel.addView(this)
            setOnClickListener {
                if (resumePolicy.wantsSending) stopSending()
                else {
                    resumePolicy.start()
                    persistSendingIntent()
                    automaticResumeMessage = ""
                    requestSending()
                }
            }
        }
        recoveryStatus = label("")
        Switch(this).apply {
            text = "起動時に送信を自動開始"
            isChecked = prefs.getBoolean(SendingResumePolicy.AUTO_START_KEY, false)
            panel.addView(this)
            setOnCheckedChangeListener { _, enabled ->
                prefs.edit().putBoolean(SendingResumePolicy.AUTO_START_KEY, enabled).apply()
            }
        }
        detection = label("停止中")
        if (BuildConfig.DEBUG) {
            Switch(this).apply {
                text = "開発用: 非Wi-Fiで送信を許可（手入力IP必須）"
                isChecked = false
                panel.addView(this)
                setOnCheckedChangeListener { _, enabled ->
                    stopSending()
                    developerNetworkOverride = enabled
                    discovery.setDeveloperNetworkOverride(enabled)
                }
            }
        }
        fun slider(title: String, initial: Int, change: (Int) -> Unit): SeekBar {
            val value = label("$title: $initial")
            return SeekBar(this).apply {
                max = 255; progress = initial
                panel.addView(this)
                setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                    override fun onProgressChanged(bar: SeekBar, progress: Int, fromUser: Boolean) {
                        value.text = "$title: $progress"; change(progress)
                    }
                    override fun onStartTrackingTouch(bar: SeekBar) = Unit
                    override fun onStopTrackingTouch(bar: SeekBar) = Unit
                })
            }
        }
        brightnessSlider = slider("認識の閾値（明るさ）", brightness) { brightness = it }
        dominanceSlider = slider("色の優位差", dominance) { dominance = it }
        label("既定値: 明るさ145 / 色の優位差25\n彩度30 / 赤・青共通。変更は停止中に行います。")
        mirrors = MirrorSettings.load { key, default -> prefs.getBoolean(key, default) }
        fun mirrorSwitch(title: String, initial: Boolean, change: (Boolean) -> Unit) = Switch(this).apply {
            text = title
            isChecked = initial
            panel.addView(this)
            setOnCheckedChangeListener { _, enabled ->
                if (!sending) {
                    change(enabled)
                    val editor = prefs.edit()
                    mirrors.save { key, value -> editor.putBoolean(key, value) }
                    editor.apply()
                }
            }
        }
        mirrorXSwitch = mirrorSwitch("左右反転", mirrors.mirrorX) { mirrors = mirrors.copy(mirrorX = it) }
        mirrorYSwitch = mirrorSwitch("上下反転", mirrors.mirrorY) { mirrors = mirrors.copy(mirrorY = it) }
        // 保存しない運営用の計測設定。アプリ起動時は必ずOFF。
        measurementSwitch = Switch(this).apply {
            text = "遅延計測モード"
            isChecked = false
            panel.addView(this)
        }
        label("計測時は ts= を付けます。片道遅延にはスマホとPCのNTP時計同期が必要です。\n受信間隔は同期不要。PCのF8で確認。変更は停止中のみ。")
        // 既定 ON。iPhone の 60fps で撮影→送信が約13ms短縮（2026-10-07 実測）。非対応端末は自動で 30fps。
        fps60Switch = Switch(this).apply {
            text = "カメラ 60fps（低遅延）"
            isChecked = prefs.getInt("cameraFps", 60) == 60
            setOnCheckedChangeListener { _, on -> prefs.edit().putInt("cameraFps", if (on) 60 else 30).apply() }
            panel.addView(this)
        }
        label("発熱や処理落ち（解析fpsの注意）が続くときだけ OFF にします。変更は停止中のみ。")
        camera = CameraSession(this, preview, sender) { message -> recoverCamera(message) }
        discovery = PcDiscovery(this) { pc, network, message ->
            destination = pc; wifi = network
            updateSendingDestination()
            pcStatus.text = if (pc == null) "PC: 未設定\n$message" else
                "PC: ${pc.name}\n${pc.address.hostAddress}（${pc.source}）\n$message"
            updateRecoveryStatus()
        }
        discovery.setStation(prefs.getString("station", "")?.takeIf { it in stations } ?: "")
        stationGroup.setOnCheckedChangeListener { group, id ->
            if (id == View.NO_ID) return@setOnCheckedChangeListener
            val station = group.findViewById<android.widget.RadioButton>(id).tag as String
            // 台を意図的に変更したときは停止してから新しい台を選ぶ。
            stopSending()
            discovery.clearPreferredPc()
            prefs.edit().putString("station", station).apply()
            discovery.setStation(station)
        }
        val savedAddress = ManualAddress.parse(prefs.getString("ip", "") ?: "")
        manualDestination = savedAddress != null
        discovery.setManual(savedAddress)
    }

    override fun onStart() {
        super.onStart()
        foreground = true
        thermalStatus = power.currentThermalStatus
        power.addThermalStatusListener(ContextCompat.getMainExecutor(this), thermalListener)
        updateHealth()
        if (resumePolicy.foreground(prefs.getBoolean(SendingResumePolicy.AUTO_START_KEY, false))) {
            automaticResumePending = true
            persistSendingIntent()
            cameraBackoff.reset()
            requestSending()
        }
        discovery.start()
        lastStats = System.nanoTime()
        main.postDelayed(refresh, 1000)
    }

    private fun persistSendingIntent() {
        // 次の異常終了にも停止操作が確実に反映されるよう同期保存する。
        prefs.edit().putBoolean(SendingResumePolicy.WAS_SENDING_KEY, resumePolicy.wantsSending).commit()
    }

    private fun requestSending() {
        if (!foreground || !resumePolicy.wantsSending) return
        startStop.text = "停止"
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            recoveryStatus.text = "カメラの許可を待っています"
            if (!permissionPending) {
                permissionPending = true
                permission.launch(Manifest.permission.CAMERA)
            }
        } else startSending()
    }

    private fun startSending() {
        if (sending || !foreground || !resumePolicy.wantsSending) return
        sending = true
        startStop.text = "停止"
        brightnessSlider.isEnabled = false; dominanceSlider.isEnabled = false
        mirrorXSwitch.isEnabled = false; mirrorYSwitch.isEnabled = false
        measurementSwitch.isEnabled = false
        fps60Switch.isEnabled = false
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        // 送信中は Wi-Fi の省電力（ビーコン待ちの送信まとめ）を止め、遅延とばらつきを減らす。
        // 低遅延モードは前面・画面点灯中だけ有効になる（Android 10+ / minSdk 29）。
        if (!lowLatencyWifi.isHeld) lowLatencyWifi.acquire()
        startCamera()
    }

    private fun updateSendingDestination() {
        if (!sending || !foreground) return
        val connectivity = getSystemService(ConnectivityManager::class.java)
        val selectedNetwork = wifi
        val isWifi = selectedNetwork?.let { connectivity.getNetworkCapabilities(it)
            ?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) } == true
        val allowed = SendingNetworkPolicy.canSend(BuildConfig.DEBUG, developerNetworkOverride,
            selectedNetwork != null, isWifi, manualDestination)
        sendingNetwork = if (allowed) selectedNetwork else null
        sender.configure(if (allowed) destination else null, sendingNetwork)
    }

    private fun startCamera() {
        if (!sending || !foreground || !resumePolicy.wantsSending) return
        cameraStarted = true
        cameraStartedAt = System.nanoTime() / 1e9
        updateSendingDestination()
        camera.start(brightness, dominance, mirrors, measurementSwitch.isChecked,
            if (fps60Switch.isChecked) 60 else 30)
    }

    private fun recoverCamera(message: String) {
        if (!sending || !foreground || cameraRetry != null || !cameraStarted) return
        cameraStarted = false
        camera.stop()
        val delay = cameraBackoff.nextDelay(System.nanoTime() / 1e9)
        if (delay == null) {
            cameraRecoveryMessage = "カメラの復旧が15分間できません。権限・端末を確認し、停止→開始してください"
        } else {
            cameraRecoveryMessage = "カメラを再接続中…（${delay.toInt()}秒後に再試行）\n$message"
            val retry = Runnable { cameraRetry = null; startCamera() }
            cameraRetry = retry
            main.postDelayed(retry, (delay * 1000).toLong())
        }
        updateRecoveryStatus()
    }

    private fun updateRecoveryStatus() {
        if (!::recoveryStatus.isInitialized) return
        val reconnect = sending && (destination == null || sendingNetwork == null || sender.error != null)
        recoveryStatus.text = listOf(automaticResumeMessage, cameraRecoveryMessage,
            if (reconnect) "PC を再接続中…" else "").filter { it.isNotEmpty() }.joinToString("\n")
    }

    private fun stopSending(clearIntent: Boolean = true) {
        if (clearIntent) {
            resumePolicy.stop()
            persistSendingIntent()
            automaticResumePending = false
            automaticResumeMessage = ""
            discovery.clearPreferredPc()
        }
        cameraRetry?.let(main::removeCallbacks); cameraRetry = null
        cameraBackoff.reset()
        cameraRecoveryMessage = ""
        sending = false
        cameraStarted = false
        sendingNetwork = null
        camera.stop()
        startStop.text = "開始"
        brightnessSlider.isEnabled = true; dominanceSlider.isEnabled = true
        mirrorXSwitch.isEnabled = true; mirrorYSwitch.isEnabled = true
        measurementSwitch.isEnabled = true
        fps60Switch.isEnabled = true
        window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        if (lowLatencyWifi.isHeld) lowLatencyWifi.release()
        detection.text = "停止中\n赤: 未検出 / 送信 0 fps\n青: 未検出 / 送信 0 fps"
        updateRecoveryStatus()
        updateHealth()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        ThresholdSettings(brightness, dominance).save(outState::putInt)
    }

    override fun onStop() {
        foreground = false
        power.removeThermalStatusListener(thermalListener)
        main.removeCallbacks(refresh)
        stopSending(clearIntent = false)
        discovery.stop()
        super.onStop()
    }

    override fun onDestroy() {
        camera.close()
        sender.close()
        super.onDestroy()
    }

    private fun updateHealth() {
        val level = ThermalLevel.fromStatus(thermalStatus)
        val rates = if (sending) camera.healthRates() else HealthRates()
        val percent = battery.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY).takeIf { it in 0..100 }
        val state = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            ?.getIntExtra(BatteryManager.EXTRA_STATUS, -1) ?: -1
        val warning = DeviceHealthText.warning(level, rates, camera.activeFps.toDouble(), sending)
        health.text = "発熱 ${level.title} / ${DeviceHealthText.battery(percent, DeviceHealthText.batteryState(state))}\n" +
            DeviceHealthText.timing(rates) + (warning?.let { "\n$it" } ?: "")
    }

    private fun toast(message: String) { Toast.makeText(this, message, Toast.LENGTH_LONG).show() }
}
