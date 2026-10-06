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
    private var destination: Destination? = null
    private var wifi: Network? = null
    private var sending = false
    private var foreground = false
    private var brightness = 145
    private var dominance = 25
    private var lastStats = 0L
    private val permission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted && foreground) startSending()
        else if (!granted) toast("カメラの許可が必要です。設定から許可してください")
    }
    private val refresh = object : Runnable {
        override fun run() {
            if (!foreground) return
            if (sending && developerNetworkOverride) {
                val connectivity = getSystemService(ConnectivityManager::class.java)
                if (connectivity.activeNetwork != sendingNetwork) {
                    stopSending()
                    toast("ネットワークが変わりました。確認して開始してください")
                }
            }
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
        sender = LatestUdpSender()
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
        val prefs = getSharedPreferences("destination", MODE_PRIVATE)
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
                if (sending) stopSending()
                else if (ContextCompat.checkSelfPermission(this@MainActivity, Manifest.permission.CAMERA)
                    != PackageManager.PERMISSION_GRANTED) permission.launch(Manifest.permission.CAMERA)
                else startSending()
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
        camera = CameraSession(this, preview, sender) { message -> stopSending(); toast(message) }
        discovery = PcDiscovery(this) { pc, network, message ->
            val connectivity = getSystemService(ConnectivityManager::class.java)
            val isWifi = network?.let { connectivity.getNetworkCapabilities(it)
                ?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) } == true
            val permitted = SendingNetworkPolicy.canSend(BuildConfig.DEBUG, developerNetworkOverride,
                network != null, isWifi, manualDestination)
            val changed = destination != pc || wifi != network || !permitted
            if (sending && changed) {
                stopSending()
                toast("送信先またはネットワークが変わりました。確認して開始してください")
            }
            destination = pc; wifi = network
            pcStatus.text = if (pc == null) "PC: 未設定\n$message" else
                "PC: ${pc.name}\n${pc.address.hostAddress}（${pc.source}）\n$message"
        }
        discovery.setStation(prefs.getString("station", "")?.takeIf { it in stations } ?: "")
        stationGroup.setOnCheckedChangeListener { group, id ->
            if (id == View.NO_ID) return@setOnCheckedChangeListener
            val station = group.findViewById<android.widget.RadioButton>(id).tag as String
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
        discovery.start()
        lastStats = System.nanoTime()
        main.postDelayed(refresh, 1000)
    }

    private fun startSending() {
        if (sending || !foreground) return
        val connectivity = getSystemService(ConnectivityManager::class.java)
        val selectedNetwork = wifi
        val isWifi = selectedNetwork?.let { connectivity.getNetworkCapabilities(it)
            ?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) } == true
        if (destination == null || !SendingNetworkPolicy.canSend(BuildConfig.DEBUG, developerNetworkOverride,
                selectedNetwork != null, isWifi, manualDestination)) {
            toast(if (BuildConfig.DEBUG) "同じWi-FiでPCを探索するか、開発用設定と手入力IPを確認してください"
                else "同じWi-FiでPCを探索するか、IPを手入力してください"); return
        }
        sendingNetwork = selectedNetwork
        sender.configure(destination, wifi)
        sending = true
        startStop.text = "停止"
        brightnessSlider.isEnabled = false; dominanceSlider.isEnabled = false
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        camera.start(brightness, dominance)
    }

    private fun stopSending() {
        sending = false
        sendingNetwork = null
        camera.stop()
        startStop.text = "開始"
        brightnessSlider.isEnabled = true; dominanceSlider.isEnabled = true
        window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        detection.text = "停止中\n赤: 未検出 / 送信 0 fps\n青: 未検出 / 送信 0 fps"
        updateHealth()
    }

    override fun onStop() {
        foreground = false
        power.removeThermalStatusListener(thermalListener)
        main.removeCallbacks(refresh)
        stopSending()
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
        val warning = DeviceHealthText.warning(level, rates, 30.0, sending)
        health.text = "発熱 ${level.title} / ${DeviceHealthText.battery(percent, DeviceHealthText.batteryState(state))}\n" +
            DeviceHealthText.timing(rates) + (warning?.let { "\n$it" } ?: "")
    }

    private fun toast(message: String) { Toast.makeText(this, message, Toast.LENGTH_LONG).show() }
}
