package jp.phonesaber.sender

import android.graphics.ImageFormat
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.CaptureResult
import android.hardware.camera2.TotalCaptureResult
import android.os.Handler
import android.os.Looper
import android.os.Process
import android.os.SystemClock
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
    // 実際に要求しているカメラ fps（60 非対応の端末・解像度では 30）。
    @Volatile var activeFps = 30
        private set
    @Volatile var diagnostics = "camera=starting"
        private set
    private val executor = analysisExecutor {
        Process.setThreadPriority(Process.THREAD_PRIORITY_MORE_FAVORABLE)
    }
    private var provider: ProcessCameraProvider? = null
    private var analysis: ImageAnalysis? = null
    private var cameraState: androidx.lifecycle.LiveData<androidx.camera.core.CameraState>? = null
    // These fields are accessed only on executor.
    private var core: NativeCore? = null
    private var expiry: ScheduledFuture<*>? = null
    private val healthMeter = sender.healthMeter
    fun healthRates(): HealthRates = healthMeter.snapshot(System.nanoTime() / 1e9)

    fun start(brightness: Int, dominance: Int, mirrors: MirrorSettings, measurementMode: Boolean = false,
              preferredFps: Int = 30, experimentalLowLatency: Boolean = false) {
        val token = synchronized(gate) {
            running = true
            status = Status()
            lastFrameAt = 0.0
            diagnostics = "camera=starting"
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
                // REALTIME のセンサー時刻だけを elapsedRealtimeNanos と比較する。
                val realtimeSensorClock = camera2.getCameraCharacteristic(
                    CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE) ==
                    CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE_REALTIME
                val ranges = camera2.getCameraCharacteristic(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)
                val map = camera2.getCameraCharacteristic(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
                val fixed60 = ranges?.contains(Range(60, 60)) == true
                // 60fps は AE の固定 [60,60] と、VGA 以下で 1/60 秒以内に出せる YUV 解像度がある場合だけ。
                val vga60 = map?.getOutputSizes(ImageFormat.YUV_420_888)?.any { size ->
                    CameraFormatPolicy.isCompact(size.width, size.height) &&
                        map.getOutputMinFrameDuration(ImageFormat.YUV_420_888, size) <= CameraFormatPolicy.FRAME_60FPS_NS
                } == true
                val fps = CameraFormatPolicy.selectFps(preferredFps, fixed60, vga60)
                require(ranges?.contains(Range(fps, fps)) == true) { "このカメラは固定30 fpsに対応していません" }
                activeFps = fps
                val maxFrameDuration = CameraFormatPolicy.maxFrameDuration(fps)
                // Preview の候補（PRIVATE）が YUV 表に無いと getOutputMinFrameDuration は例外を投げ、
                // bind 全体が失敗する。表に無いサイズは従来の「不明」と同じ扱いにする。
                fun yuvMinFrameDuration(size: android.util.Size): Long? = try {
                    map?.getOutputMinFrameDuration(ImageFormat.YUV_420_888, size)
                } catch (_: IllegalArgumentException) { null }
                val selector = ResolutionSelector.Builder()
                    .setResolutionFilter { sizes, _ ->
                        // Same iPhone policy: largest <= VGA at the requested fps, otherwise smallest.
                        CameraFormatPolicy.order(sizes, { it.width }, { it.height }, ::yuvMinFrameDuration,
                            maxFrameDuration)
                    }.build()
                val builder = ImageAnalysis.Builder()
                    .setTargetRotation(Surface.ROTATION_0) // Fixed portrait independent of physical/device UI rotation.
                    .setResolutionSelector(selector)
                    .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
                    .setOutputImageRotationEnabled(false) // JNI 内で同じ向きへ byte 単位で回転。
                    // この方式ではsetImageQueueDepthは無効。FIFOを作らず最新だけを解析する。
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                val interop = Camera2Interop.Extender(builder)
                    .setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, Range(fps, fps))
                    .setCaptureRequestOption(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
                    .setCaptureRequestOption(CaptureRequest.CONTROL_AE_LOCK, false)
                    .setCaptureRequestOption(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE,
                        CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_OFF)
                    .setCaptureRequestOption(CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE,
                        CaptureRequest.LENS_OPTICAL_STABILIZATION_MODE_OFF)
                // 認識に渡る画素が変わるので、試験設定ONかつOFF対応のときだけ指定する。
                CameraPostProcessingPolicy.offMode(experimentalLowLatency,
                    camera2.getCameraCharacteristic(CameraCharacteristics.NOISE_REDUCTION_AVAILABLE_NOISE_REDUCTION_MODES),
                    CaptureRequest.NOISE_REDUCTION_MODE_OFF)?.let {
                    interop.setCaptureRequestOption(CaptureRequest.NOISE_REDUCTION_MODE, it)
                }
                CameraPostProcessingPolicy.offMode(experimentalLowLatency,
                    camera2.getCameraCharacteristic(CameraCharacteristics.EDGE_AVAILABLE_EDGE_MODES),
                    CaptureRequest.EDGE_MODE_OFF)?.let {
                    interop.setCaptureRequestOption(CaptureRequest.EDGE_MODE, it)
                }
                var nextDiagnosticAt = 0.0
                var streamDiagnostics = "streams=starting"
                interop.setSessionCaptureCallback(object : CameraCaptureSession.CaptureCallback() {
                    override fun onCaptureCompleted(session: CameraCaptureSession, request: CaptureRequest,
                                                    result: TotalCaptureResult) {
                        val now = System.nanoTime() / 1e9
                        synchronized(gate) {
                            if (!running || token != generation) return
                            healthMeter.captured(now, result.frameNumber)
                            // CaptureResultは実際のHAL設定。毎フレームの文字列生成を避ける。
                            if (now >= nextDiagnosticAt) {
                                nextDiagnosticAt = now + 1.0
                                diagnostics = "camera_generation=$token clock=${if (realtimeSensorClock) "REALTIME" else "UNKNOWN"} " +
                                    "experimental=$experimentalLowLatency " +
                                    "fixed60=$fixed60 compact60=$vga60 $streamDiagnostics " +
                                    "nr=${result.get(CaptureResult.NOISE_REDUCTION_MODE)} " +
                                    "edge=${result.get(CaptureResult.EDGE_MODE)} " +
                                    "video_stabilization=${result.get(CaptureResult.CONTROL_VIDEO_STABILIZATION_MODE)} " +
                                    "ois=${result.get(CaptureResult.LENS_OPTICAL_STABILIZATION_MODE)} " +
                                    "exposure_ns=${result.get(CaptureResult.SENSOR_EXPOSURE_TIME)} " +
                                    "frame_duration_ns=${result.get(CaptureResult.SENSOR_FRAME_DURATION)} " +
                                    "ae_fps=${result.get(CaptureResult.CONTROL_AE_TARGET_FPS_RANGE)}"
                            }
                        }
                    }
                })
                val frames = builder.build()
                analysis = frames
                frames.setAnalyzer(executor) { image ->
                    val analyzerStartedNs = SystemClock.elapsedRealtimeNanos()
                    try {
                        if (running && token == generation) {
                            val started = System.nanoTime()
                            val plane = image.planes.single()
                            // No viewport crop, resize, channel swap or mirroring.
                            val processor = core ?: return@setAnalyzer
                            val jniStarted = System.nanoTime()
                            val degrees = image.imageInfo.rotationDegrees
                            val results = processor.processCamera(plane.buffer, image.width, image.height,
                                plane.rowStride, plane.pixelStride, degrees, started / 1e9, brightness, dominance)
                            val jniEnded = System.nanoTime()
                            val jniEndedNs = SystemClock.elapsedRealtimeNanos()
                            val capturedNs = CaptureClock.timestamp(image.imageInfo.timestamp, realtimeSensorClock)
                            synchronized(gate) {
                                if (running && token == generation) {
                                    lastFrameAt = jniEnded / 1e9
                                    healthMeter.processed(jniEnded / 1e9, (jniEnded - jniStarted) / 1e6)
                                    sender.offer(results, started, capturedNs, analyzerStartedNs, jniEndedNs)
                                    val width = if (degrees % 180 == 0) image.width else image.height
                                    val height = if (degrees % 180 == 0) image.height else image.width
                                    status = describe(results, "${width}×${height} / $fps fps要求")
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
                // 選択された実サイズを記録。AE表だけではPreview併用時の実60fpsは保証できない。
                val analysisSize = frames.resolutionInfo?.resolution
                val previewSize = preview.resolutionInfo?.resolution
                val analysisDuration = analysisSize?.let(::yuvMinFrameDuration)
                val previewDuration = try {
                    previewSize?.let { map?.getOutputMinFrameDuration(ImageFormat.PRIVATE, it) }
                } catch (_: IllegalArgumentException) { null }
                synchronized(gate) {
                    streamDiagnostics = "format=RGBA_8888 analysis_size=$analysisSize " +
                        "analysis_yuv_min_frame_ns=$analysisDuration " +
                        "preview_size=$previewSize preview_private_min_frame_ns=$previewDuration"
                }
                cameraState = camera.cameraInfo.cameraState
                cameraState?.observe(owner) { state ->
                    state.error?.let { fail(token, CameraErrorText.describe(it.code, fps)) }
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
