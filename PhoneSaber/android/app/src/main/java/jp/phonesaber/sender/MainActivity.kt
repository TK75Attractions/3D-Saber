package jp.phonesaber.sender

import android.Manifest
import android.content.pm.PackageManager
import android.content.Intent
import android.content.IntentFilter
import android.content.ClipData
import android.content.ClipboardManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.BatteryManager
import android.os.Build
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
import java.text.SimpleDateFormat
import java.util.Date

private const val IGNORE_TAG = "PhoneSaberIgnore"
private const val AREA_TAG = "PhoneSaberArea"

class MainActivity : ComponentActivity() {
    private val main = Handler(Looper.getMainLooper())
    private lateinit var sender: LatestUdpSender
    private val lowLatencyWifi by lazy {
        applicationContext.getSystemService(WifiManager::class.java)
            .createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "PhoneSaber:sending")
            .apply { setReferenceCounted(false) }
    }
    @Suppress("DEPRECATION")
    private val highPerformanceWifi by lazy {
        applicationContext.getSystemService(WifiManager::class.java)
            .createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "PhoneSaber:sending-perf")
            .apply { setReferenceCounted(false) }
    }
    private val latencyLog = LatencyLog()
    private val metricsCadence = MetricsCadence()
    private val logTime = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ssXXX", Locale.US)
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
    private lateinit var lowLatencyCameraSwitch: Switch
    private var mirrors = MirrorSettings()
    private var destination: Destination? = null
    private var wifi: Network? = null
    private var sending = false
    private lateinit var resumePolicy: SendingResumePolicy
    private lateinit var launchSettings: LaunchSettings
    private val prefs by lazy { getSharedPreferences("destination", MODE_PRIVATE) }
    private val cameraBackoff = RecoveryBackoff()
    private val pcReplyWatch = PcReplyWatch()
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
    // 無視範囲（会場のコーンなど）。main だけが変更し、CameraSession へ不変オブジェクトで渡す。
    private var ignoreZones = IgnoreZones.NONE
    private lateinit var ignoreSummary: TextView
    private lateinit var zoneOverlay: IgnoreZoneOverlay
    private val seenPcCommandIds = mutableSetOf<Int>()
    private lateinit var trackLockSwitch: Switch
    // プレイ範囲（剣を振る場所）と露出補正。main だけが変更し、CameraSession へ渡す。
    private var playArea: PlayArea? = null
    private lateinit var areaSummary: TextView
    private var areaLearningUntil = 0.0
    private var areaLearningToken = 0
    private lateinit var exposureSummary: TextView
    private var exposureBias = 0
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
            val healthRates = updateHealth()
            val now = System.nanoTime()
            val elapsed = (now - lastStats) / 1e9
            val red = sender.redSent.getAndSet(0) / elapsed
            val blue = sender.blueSent.getAndSet(0) / elapsed
            lastStats = now
            val seconds = now / 1e9
            if (metricsCadence.due(seconds, sending && sendingNetwork != null)) destination?.let { pc ->
                val rates = healthRates
                val rtt = discovery.metricsRoundTrip(pc.address, seconds)
                val thermal = when (ThermalLevel.fromStatus(thermalStatus)) {
                    ThermalLevel.NORMAL -> "nominal"; ThermalLevel.ELEVATED -> "fair"
                    ThermalLevel.HIGH -> "serious"; ThermalLevel.DANGEROUS -> "critical"
                    ThermalLevel.UNKNOWN -> "unknown"
                }
                MetricsDatagram(station = prefs.getString("station", "") ?: "", app = BuildConfig.VERSION_CODE.toString(),
                    fpsMode = camera.activeFps.toDouble(), captureFps = rates.captureFps,
                    redFps = rates.redSendFps, blueFps = rates.blueSendFps,
                    median = rates.medianCaptureToSendMs, p95 = rates.p95CaptureToSendMs, samples = rates.sentSamples,
                    rtt = rtt.first, rttSamples = rtt.second, rttAge = rtt.third, thermal = thermal,
                    battery = battery.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY).toDouble(),
                    lowLatencyTest = lowLatencyCameraSwitch.isChecked).line()?.let { discovery.sendMetrics(pc.address, it) }
            }
            if (latencyLog.due(seconds, sending)) {
                val line = "${logTime.format(Date())} device=${Build.MODEL} api=${Build.VERSION.SDK_INT} " +
                    "requested_fps=${camera.activeFps} ${camera.status.dimensions} " +
                    LatencyStatusText.line(camera.healthRates(), thermalStatus, pcReplyWatch.ageSeconds(seconds)) +
                    " ${camera.diagnostics} wifi_low_latency=${lowLatencyWifi.isHeld} wifi_high_perf=${highPerformanceWifi.isHeld}"
                Log.i(LatencyLog.TAG, line)
                latencyLog.append(seconds, line)
            }
            zoneOverlay.update(ignoreZones, camera.frameSize, playArea)
            updateAreaSummary()
            exposureSummary.text = camera.exposureStatus
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
        @Suppress("DEPRECATION")
        val settings = LaunchSettings.read { key -> intent.extras?.get(key) }
        launchSettings = settings
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
        // プレビューの上に無視範囲の枠を重ねる（プレビューの実装・表示方法は変えない）。
        zoneOverlay = IgnoreZoneOverlay(this)
        val previewFrame = android.widget.FrameLayout(this).apply {
            addView(preview, android.widget.FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT))
            addView(zoneOverlay, android.widget.FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT))
        }
        panel.addView(previewFrame, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(250)))
        health = label("端末状態を確認中")
        Button(this).apply {
            text = "ログをコピー"
            panel.addView(this)
            setOnClickListener {
                val text = latencyLog.copy(System.nanoTime() / 1e9)
                if (text.isEmpty()) toast("送信開始から10秒後にログが記録されます")
                else {
                    getSystemService(ClipboardManager::class.java)
                        .setPrimaryClip(ClipData.newPlainText("PhoneSaber latency", text))
                    toast("直近5分のログをコピーしました")
                }
            }
        }
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
            isChecked = launchSettings.autoStart(prefs.getBoolean(SendingResumePolicy.AUTO_START_KEY, false))
            panel.addView(this)
            setOnCheckedChangeListener { _, enabled ->
                prefs.edit().putBoolean(SendingResumePolicy.AUTO_START_KEY, enabled).apply()
            }
        }
        detection = label("停止中")
        // 会場のコーンなど、剣ではない赤・青の物を無視する（iPhone と同じ。PC の F8 からも操作できる）。
        ignoreSummary = label("")
        val ignoreRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        fun ignoreButton(title: String, action: () -> Unit) = Button(this).apply {
            text = title; isAllCaps = false
            setOnClickListener { action() }
            ignoreRow.addView(this, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        }
        ignoreButton("今の赤を無視（剣を消して押す）") { ignoreCurrent(0, null) }
        ignoreButton("今の青を無視") { ignoreCurrent(1, null) }
        ignoreButton("すべて消す") { clearIgnoreZones(null) }
        panel.addView(ignoreRow)
        trackLockSwitch = Switch(this).apply {
            text = "剣を追いかける（通行人対策・試験）"
            isChecked = prefs.getBoolean("trackLock.v1", false)
            setOnCheckedChangeListener { _, enabled -> setTrackLockEnabled(enabled) }
            panel.addView(this)
        }
        label("送信中に、剣の電源を切るかカメラの外に出し、コーンなどだけが認識されている状態で押します。" +
            "以後その範囲の候補は選ばず、同じコマの剣を選びます。カメラや物を動かしたら押し直してください（色ごとに最大4つ）。")
            .apply { textSize = 13f }
        // プレイ範囲（赤い服の人など、動く赤・青の物の対策。iPhone と同じ。PC の F8 からも操作できる）。
        areaSummary = label("")
        val areaRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        fun areaButton(title: String, action: () -> Unit) = Button(this).apply {
            text = title; isAllCaps = false
            setOnClickListener { action() }
            areaRow.addView(this, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        }
        areaButton("プレイ範囲を覚える（${PlayArea.LEARN_SECONDS}秒振る）") { learnPlayArea(PlayArea.LEARN_SECONDS, null) }
        areaButton("プレイ範囲を消す") { clearPlayArea(null) }
        panel.addView(areaRow)
        label("送信中に押し、${PlayArea.LEARN_SECONDS}秒間、遊ぶ範囲いっぱいに剣（赤・青）を振ります。" +
            "以後、中心がその範囲（緑の枠）の外にある候補は選びません。カメラを動かしたら覚え直してください。")
            .apply { textSize = 13f }
        // 露出補正（試験。既定 0 = OFF）。負にすると光らない服などが暗くなる。
        exposureSummary = label("")
        val exposureRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        for (ev in ExposureBias.CHOICES) Button(this).apply {
            text = "露出 ${if (ev == 0) "0" else "−${-ev}"}"; isAllCaps = false
            setOnClickListener { setExposureBias(ev, null) }
            exposureRow.addView(this, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        }
        panel.addView(exposureRow)
        label("既定 0（OFF、カメラの露出に触れない）。−1・−2 は光らない服などを暗くする試験設定で、認識に入る画像が変わります。" +
            "送信中もそのまま切り替わり、保存されます。").apply { textSize = 13f }
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
            isChecked = launchSettings.cameraFps(prefs.getInt("cameraFps", 60)) == 60
            setOnCheckedChangeListener { _, on -> prefs.edit().putInt("cameraFps", if (on) 60 else 30).apply() }
            panel.addView(this)
        }
        label("暗い場所では60fps固定により画像が暗くなる場合があります。暗さ・発熱・処理落ちが続くときは OFF で30fpsと比較します。変更は停止中のみ。")
        // 保存しない。今回の起動引数がなければOFFに戻す。
        lowLatencyCameraSwitch = Switch(this).apply {
            text = "低遅延カメラ設定（試験）"
            isChecked = launchSettings.lowLatencyCamera
            panel.addView(this)
        }
        label("ノイズ低減・輪郭強調を対応端末で OFF にします。画質と認識結果が変わる可能性があります。既定 OFF。変更は停止中のみ。")
        camera = CameraSession(this, preview, sender) { message -> recoverCamera(message) }
        camera.trackLockEnabled = trackLockSwitch.isChecked
        setIgnoreZones(IgnoreZones.decode(prefs.getString(IgnoreZones.PREFERENCE_KEY, null)), persist = false)
        setPlayArea(PlayArea.decode(prefs.getString(PlayArea.PREFERENCE_KEY, null)), persist = false)
        setExposureBias(prefs.getInt(ExposureBias.PREFERENCE_KEY, 0).takeIf { ExposureBias.valid(it) } ?: 0, null,
            persist = false)
        discovery = PcDiscovery(this, onReply = { address, now ->
            address.hostAddress?.let { pcReplyWatch.received(it, now) }
            updateRecoveryStatus()
        }, onUpdate = { pc, network, message ->
            destination = pc; wifi = network
            updateSendingDestination()
            pcStatus.text = if (pc == null) "PC: 未設定\n$message" else
                "PC: ${pc.name}\n${pc.address.hostAddress}（${pc.source}）\n$message"
            updateRecoveryStatus()
        }, onCommand = { from, command -> applyPcCommand(from, command) })
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
        if (resumePolicy.foreground(launchSettings.autoStart(prefs.getBoolean(SendingResumePolicy.AUTO_START_KEY, false)))) {
            automaticResumePending = true
            if (launchSettings.persistAutomaticResume) persistSendingIntent()
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
        lowLatencyCameraSwitch.isEnabled = false
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        // 送信中は Wi-Fi の省電力（ビーコン待ちの送信まとめ）を止め、遅延とばらつきを減らす。
        // 低遅延モードは前面・画面点灯中だけ有効になる（Android 10+ / minSdk 29）。
        // lockのRPC失敗でも送信を開始し、片方の失敗でももう片方は取得を試す。
        try { if (!lowLatencyWifi.isHeld) lowLatencyWifi.acquire() } catch (_: RuntimeException) { }
        try { if (!highPerformanceWifi.isHeld) highPerformanceWifi.acquire() } catch (_: RuntimeException) { }
        latencyLog.start(System.nanoTime() / 1e9)
        startCamera()
        updateRecoveryStatus()
    }

    private fun updateSendingDestination() {
        if (!sending || !foreground) return
        val connectivity = getSystemService(ConnectivityManager::class.java)
        val selectedNetwork = wifi
        val isWifi = selectedNetwork?.let { connectivity.getNetworkCapabilities(it)
            ?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) } == true
        val allowed = SendingNetworkPolicy.canSend(BuildConfig.DEBUG, developerNetworkOverride,
            selectedNetwork != null, isWifi, manualDestination)
        val nextNetwork = if (allowed) selectedNetwork else null
        if (sendingNetwork != nextNetwork) pcReplyWatch.reset()
        sendingNetwork = nextNetwork
        sender.configure(if (allowed) destination else null, sendingNetwork)
    }

    private fun startCamera() {
        if (!sending || !foreground || !resumePolicy.wantsSending) return
        cameraStarted = true
        cameraStartedAt = System.nanoTime() / 1e9
        updateSendingDestination()
        camera.start(brightness, dominance, mirrors, measurementSwitch.isChecked,
            if (fps60Switch.isChecked) 60 else 30, lowLatencyCameraSwitch.isChecked)
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
        val replyWarning = pcReplyWatch.update(sending, destination?.address?.hostAddress,
            manualDestination, System.nanoTime() / 1e9)
        recoveryStatus.text = listOf(automaticResumeMessage, cameraRecoveryMessage,
            if (reconnect) "PC を再接続中…" else "", replyWarning ?: "")
            .filter { it.isNotEmpty() }.joinToString("\n")
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
        lowLatencyCameraSwitch.isEnabled = true
        window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        // 解放RPCの失敗で他方の解放・停止を中断しない。保持中なら次の停止で再試行する。
        try { if (lowLatencyWifi.isHeld) lowLatencyWifi.release() } catch (_: RuntimeException) { }
        try { if (highPerformanceWifi.isHeld) highPerformanceWifi.release() } catch (_: RuntimeException) { }
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

    private fun updateHealth(): HealthRates {
        val level = ThermalLevel.fromStatus(thermalStatus)
        val rates = if (sending) camera.healthRates() else HealthRates()
        val percent = battery.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY).takeIf { it in 0..100 }
        val state = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            ?.getIntExtra(BatteryManager.EXTRA_STATUS, -1) ?: -1
        val warning = DeviceHealthText.warning(level, rates, camera.activeFps.toDouble(), sending)
        health.text = "発熱 ${level.title} / ${DeviceHealthText.battery(percent, DeviceHealthText.batteryState(state))}\n" +
            DeviceHealthText.timing(rates) + (warning?.let { "\n$it" } ?: "")
        return rates
    }

    /** 今その色で選ばれている候補（剣を消していればコーンなど）の範囲を無視範囲に加える。 */
    private fun ignoreCurrent(color: Int, commandId: Int?) {
        val name = if (color == 0) "赤" else "青"
        camera.captureWinnerZone(color) { zone ->
            if (zone == null) {
                Log.i(IGNORE_TAG, "add color=$color id=$commandId: no current detection")
                toast(if (sending) "今の${name}が認識されていません" else "送信中に押してください（今の${name}を認識中のときだけ）")
                return@captureWinnerZone
            }
            setIgnoreZones(ignoreZones.adding(color, zone))
            Log.i(IGNORE_TAG, "add color=$color id=$commandId zone=$zone")
            toast("${name}の範囲を無視します（${ignoreZones.summary}）")
        }
    }

    private fun clearIgnoreZones(commandId: Int?) {
        setIgnoreZones(IgnoreZones.NONE)
        Log.i(IGNORE_TAG, "clear id=$commandId")
    }

    private fun setIgnoreZones(zones: IgnoreZones, persist: Boolean = true) {
        ignoreZones = zones
        camera.ignoreZones = zones // 次のフレームから効く（カメラの再起動なし）
        if (persist) prefs.edit().putString(IgnoreZones.PREFERENCE_KEY, zones.encode()).apply()
        ignoreSummary.text = "無視する範囲: ${zones.summary}"
        zoneOverlay.update(zones, camera.frameSize)
    }

    private fun setTrackLockEnabled(enabled: Boolean) {
        camera.trackLockEnabled = enabled
        prefs.edit().putBoolean("trackLock.v1", enabled).apply()
        if (trackLockSwitch.isChecked != enabled) trackLockSwitch.isChecked = enabled
    }

    /** PC の F8 から届いた指示。今送っている PC からのものだけ、同じ id は一度だけ実行する。 */
    private fun applyPcCommand(from: java.net.InetAddress, command: PcCommand) {
        if (destination?.address != from || !seenPcCommandIds.add(command.id)) return
        if (command.clearIgnore) clearIgnoreZones(command.id)
        command.ignore?.let {
            if (it == "both") { ignoreCurrent(0, command.id); ignoreCurrent(1, command.id) }
            else ignoreCurrent(if (it == "blue") 1 else 0, command.id)
        }
        command.learnAreaSeconds?.let { learnPlayArea(it, command.id) }
        if (command.clearArea) clearPlayArea(command.id)
        command.exposureBias?.let { setExposureBias(it, command.id) }
        command.trackLock?.let { setTrackLockEnabled(it) }
    }

    /**
     * プレイ範囲を覚える。[seconds] 秒の間に選ばれた winner（赤・青）の両端を数え、
     * 足りれば（${PlayArea.MINIMUM_SAMPLES} コマ以上）範囲にして保存する。足りなければ前の範囲を残す。
     */
    private fun learnPlayArea(seconds: Int, commandId: Int?) {
        if (!sending) {
            Log.i(AREA_TAG, "learn id=$commandId: not sending")
            toast("送信中に押してください（プレイ範囲は送信中の認識から覚えます）")
            return
        }
        val token = ++areaLearningToken
        areaLearningUntil = System.nanoTime() / 1e9 + seconds
        camera.startAreaLearning()
        Log.i(AREA_TAG, "learn start id=$commandId seconds=$seconds")
        updateAreaSummary()
        main.postDelayed({
            if (token != areaLearningToken) return@postDelayed
            camera.finishAreaLearning { result ->
                if (token != areaLearningToken) return@finishAreaLearning
                areaLearningUntil = 0.0
                val area = result?.area
                when {
                    result == null -> {
                        Log.i(AREA_TAG, "learn id=$commandId: not learning (sending stopped or camera restarted); kept $playArea")
                        toast("プレイ範囲を覚えられませんでした（途中で送信・カメラが止まりました）。前の範囲のままです")
                    }
                    area == null -> {
                        Log.i(AREA_TAG, "learn id=$commandId: only ${result.samples} samples (< ${PlayArea.MINIMUM_SAMPLES}); kept $playArea")
                        toast("剣がほとんど見えませんでした（${result.samples} コマ）。前の範囲のままです")
                    }
                    else -> {
                        setPlayArea(area)
                        Log.i(AREA_TAG, "learn id=$commandId samples=${result.samples} area=${area.encode()}")
                        toast("プレイ範囲を覚えました（${area.summary}）")
                    }
                }
                updateAreaSummary()
            }
        }, seconds * 1000L)
    }

    private fun clearPlayArea(commandId: Int?) {
        if (areaLearningUntil > 0) {
            ++areaLearningToken; areaLearningUntil = 0.0
            camera.finishAreaLearning { }
        }
        setPlayArea(null)
        Log.i(AREA_TAG, "clear id=$commandId")
    }

    private fun setPlayArea(area: PlayArea?, persist: Boolean = true) {
        playArea = area
        camera.playArea = area // 次のフレームから効く（カメラの再起動なし）
        if (persist) prefs.edit().apply {
            if (area == null) remove(PlayArea.PREFERENCE_KEY) else putString(PlayArea.PREFERENCE_KEY, area.encode())
        }.apply()
        updateAreaSummary()
        zoneOverlay.update(ignoreZones, camera.frameSize, area)
    }

    private fun updateAreaSummary() {
        val left = areaLearningUntil - System.nanoTime() / 1e9
        areaSummary.text = "プレイ範囲: " + (playArea?.summary ?: "なし（全体）") +
            if (areaLearningUntil > 0) String.format(Locale.JAPAN, "　覚えています… あと %.0f 秒", maxOf(0.0, left)) else ""
    }

    /** 露出補正（EV）。保存し、送信中ならそのまま適用する。 */
    private fun setExposureBias(ev: Int, commandId: Int?, persist: Boolean = true) {
        if (!ExposureBias.valid(ev)) return
        exposureBias = ev
        if (persist) prefs.edit().putInt(ExposureBias.PREFERENCE_KEY, ev).apply()
        camera.setExposureBias(ev)
        exposureSummary.text = camera.exposureStatus
        Log.i(CameraSession.EXPOSURE_TAG, "set ev=$ev id=$commandId")
    }

    private fun toast(message: String) { Toast.makeText(this, message, Toast.LENGTH_LONG).show() }
}
