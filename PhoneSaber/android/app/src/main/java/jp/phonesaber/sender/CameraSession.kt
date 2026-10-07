package jp.phonesaber.sender

import android.graphics.ImageFormat
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CaptureRequest
import android.os.Handler
import android.os.Looper
import android.util.Range
import android.view.Surface
import androidx.camera.camera2.interop.Camera2CameraInfo
import androidx.camera.camera2.interop.Camera2Interop
import androidx.camera.camera2.interop.ExperimentalCamera2Interop
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleOwner
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit

@androidx.annotation.OptIn(markerClass = [ExperimentalCamera2Interop::class])
class CameraSession(private val owner: LifecycleOwner, private val previewView: PreviewView,
                    private val sender: LatestUdpSender, private val onFailure: (String) -> Unit) {
    data class Status(val red: String = "未検出", val blue: String = "未検出", val dimensions: String = "")
    private val main = Handler(Looper.getMainLooper())
    private val gate = Any()
    @Volatile private var generation = 0
    @Volatile private var running = false
    @Volatile var lastFrameAt = 0.0
        private set
    @Volatile var status = Status()
        private set
    private val executor = ScheduledThreadPoolExecutor(1).apply { removeOnCancelPolicy = true }
    private var provider: ProcessCameraProvider? = null
    private var analysis: ImageAnalysis? = null
    private var cameraState: androidx.lifecycle.LiveData<androidx.camera.core.CameraState>? = null
    // These fields are accessed only on executor.
    private var core: NativeCore? = null
    private var expiry: ScheduledFuture<*>? = null
    private val rotation = RotationHelper()
    private val healthMeter = DeviceHealthMeter()
    fun healthRates(): HealthRates = healthMeter.snapshot(System.nanoTime() / 1e9)

    fun start(brightness: Int, dominance: Int, mirrors: MirrorSettings, measurementMode: Boolean = false) {
        val token = synchronized(gate) {
            running = true
            status = Status()
            lastFrameAt = 0.0
            healthMeter.reset(System.nanoTime() / 1e9)
            ++generation
        }
        executor.execute {
            expiry?.cancel(false); expiry = null
            core?.close(); core = null
            if (running && token == generation) {
                try { core = NativeCore(mirrors, measurementMode) }
                catch (e: LinkageError) { fail(token, "ネイティブライブラリを読み込めません: ${e.localizedMessage}") }
                catch (e: Exception) { fail(token, "コア初期化失敗: ${e.localizedMessage}") }
            }
        }
        val future = ProcessCameraProvider.getInstance(previewView.context)
        future.addListener({
            if (!running || token != generation) return@addListener
            try {
                val cameras = future.get()
                provider = cameras
                val info = CameraSelector.DEFAULT_BACK_CAMERA.filter(cameras.availableCameraInfos).firstOrNull()
                    ?: error("背面カメラがありません")
                val camera2 = Camera2CameraInfo.from(info)
                val ranges = camera2.getCameraCharacteristic(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)
                require(ranges?.contains(Range(30, 30)) == true) { "このカメラは固定30 fpsに対応していません" }
                val map = camera2.getCameraCharacteristic(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
                val selector = ResolutionSelector.Builder()
                    .setResolutionFilter { sizes, _ ->
                        val supported = sizes.filter { size ->
                            val duration = map?.getOutputMinFrameDuration(ImageFormat.YUV_420_888, size) ?: 0L
                            duration == 0L || duration <= 33_333_334L
                        }
                        val compact = supported.filter { maxOf(it.width, it.height) <= 640 &&
                            minOf(it.width, it.height) <= 480 }
                        // Same iPhone policy: largest <= VGA at 30fps, otherwise smallest.
                        if (compact.isNotEmpty()) compact.sortedByDescending { it.width.toLong() * it.height }
                        else supported.sortedBy { it.width.toLong() * it.height }
                    }.build()
                val builder = ImageAnalysis.Builder()
                    .setTargetRotation(Surface.ROTATION_0) // Fixed portrait independent of physical/device UI rotation.
                    .setResolutionSelector(selector)
                    .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
                    .setOutputImageRotationEnabled(false) // RotationHelper performs exact byte rotation once.
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                Camera2Interop.Extender(builder)
                    .setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, Range(30, 30))
                    .setCaptureRequestOption(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
                    .setCaptureRequestOption(CaptureRequest.CONTROL_AE_LOCK, false)
                    .setCaptureRequestOption(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE,
                        CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_OFF)
                    .setCaptureRequestOption(CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE,
                        CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE_OFF)
                val frames = builder.build()
                analysis = frames
                frames.setAnalyzer(executor) { image ->
                    try {
                        if (running && token == generation) {
                            val started = System.nanoTime()
                            val plane = image.planes.single()
                            // No viewport crop, resize, channel swap or mirroring.
                            val pixels = rotation.orient(plane.buffer, image.width, image.height,
                                plane.rowStride, plane.pixelStride, image.imageInfo.rotationDegrees)
                            val processor = core ?: return@setAnalyzer
                            val jniStarted = System.nanoTime()
                            val results = processor.process(pixels, started / 1e9, brightness, dominance)
                            val jniEnded = System.nanoTime()
                            synchronized(gate) {
                                if (running && token == generation) {
                                    lastFrameAt = jniEnded / 1e9
                                    healthMeter.processed(jniEnded / 1e9, (jniEnded - jniStarted) / 1e6)
                                    sender.offer(results, started)
                                    status = describe(results, "${pixels.width}×${pixels.height} / 30 fps要求")
                                }
                            }
                            scheduleExpiry(token)
                        }
                    } catch (e: Exception) { fail(token, "フレーム処理失敗: ${e.localizedMessage}") }
                    finally { image.close() }
                }
                val preview = Preview.Builder().setTargetRotation(Surface.ROTATION_0)
                    .setResolutionSelector(selector).build()
                preview.setSurfaceProvider(previewView.surfaceProvider)
                cameras.unbindAll()
                val camera = cameras.bindToLifecycle(owner, CameraSelector.DEFAULT_BACK_CAMERA, frames, preview)
                cameraState = camera.cameraInfo.cameraState
                cameraState?.observe(owner) { state ->
                    state.error?.let { fail(token, "カメラエラー (${it.code})") }
                }
            } catch (e: Exception) { fail(token, "カメラ開始失敗: ${e.localizedMessage}") }
        }, ContextCompat.getMainExecutor(previewView.context))
    }

    private fun describe(results: Array<NativeResult>, dimensions: String): Status {
        fun color(index: Int): String {
            val result = results.firstOrNull { it.color == index } ?: return "未検出"
            return if (!result.fresh) "保持（送信なし）" else if (result.predicted) "予測" else "検出"
        }
        return Status(color(0), color(1), dimensions)
    }

    private fun scheduleExpiry(token: Int) {
        expiry?.cancel(false); expiry = null
        if (!running || token != generation) return
        val deadline = core?.nextExpiry() ?: return
        if (deadline < 0) return
        val delay = maxOf(0L, (deadline * 1e9 - System.nanoTime()).toLong())
        expiry = executor.schedule({
            if (running && token == generation) {
                val results = core?.expire(System.nanoTime() / 1e9) ?: return@schedule
                synchronized(gate) {
                    if (running && token == generation) status = describe(results, status.dimensions)
                }
                // Expiry is preview-only; never offer/send a datagram or synthetic missing frame.
                scheduleExpiry(token)
            }
        }, delay, TimeUnit.NANOSECONDS)
    }

    private fun fail(token: Int, message: String) {
        main.post { if (running && token == generation) onFailure(message) }
    }

    fun stop() {
        synchronized(gate) {
            running = false; generation++
            sender.stop()
            status = Status()
        }
        cameraState?.removeObservers(owner); cameraState = null
        analysis?.clearAnalyzer(); analysis = null
        provider?.unbindAll()
        executor.execute {
            expiry?.cancel(false); expiry = null
            core?.close(); core = null
        }
    }

    fun close() {
        stop()
        // Drain already-delivered analyzers (which close their ImageProxy) and native destruction.
        executor.shutdown()
    }
}
