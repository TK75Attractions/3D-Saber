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
import android.util.Log
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
    // 無視範囲（main で差し替え、解析スレッドが毎フレーム読む不変オブジェクト）。カメラの再起動は不要。
    @Volatile var trackLockEnabled = false
    @Volatile var ignoreZones: IgnoreZones = IgnoreZones.NONE
    // 解析フレーム（縦向きに回転後）の寸法。プレビュー上に無視範囲を描くため。
    @Volatile var frameSize: Pair<Int, Int>? = null
        private set
    // プレイ範囲（main で差し替え、解析スレッドが毎フレーム読む不変オブジェクト）。null = 範囲なし（従来と同じ）。
    @Volatile var playArea: PlayArea? = null
    // 露出補正（EV、0/-1/-2）。0 の間はカメラの露出に触れない。boundCamera・exposureTouched は main だけが触る。
    @Volatile var exposureBiasEv = 0
        private set
    @Volatile var exposureStatus = "露出補正: ${ExposureBias.label(0)}"
        private set
    private var boundCamera: androidx.camera.core.Camera? = null
    // この CameraSession で 0 以外を一度でも設定したか（0 に戻すときだけ 0 を書き込む）。
    private var exposureTouched = false
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
        // Futureを返す前の初期化・RPC失敗も、同じ世代の自動復旧へ渡す。
        val future = try { ProcessCameraProvider.getInstance(previewView.context) }
        catch (e: Exception) { fail(token, "カメラ開始失敗: ${e.localizedMessage}"); return }
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
                                    "ae_fps=${result.get(CaptureResult.CONTROL_AE_TARGET_FPS_RANGE)} " +
                                    "ae_compensation=${result.get(CaptureResult.CONTROL_AE_EXPOSURE_COMPENSATION)}"
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
                                plane.rowStride, plane.pixelStride, degrees, started / 1e9, brightness, dominance,
                                ignoreZones, playArea, trackLockEnabled)
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
                                    if (frameSize?.let { it.first != width || it.second != height } != false)
                                        frameSize = width to height
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
                // 再 bind（送信の開始・カメラ復旧）のたびに露出補正をかけ直す。0 で未変更なら何もしない。
                boundCamera = camera
                applyExposureBias("bind")
                cameraState = camera.cameraInfo.cameraState
                cameraState?.observe(owner) { state ->
                    state.error?.let { fail(token, CameraErrorText.describe(it.code, fps)) }
                }
            } catch (e: Exception) { fail(token, "カメラ開始失敗: ${e.localizedMessage}") }
        }, ContextCompat.getMainExecutor(previewView.context))
    }

    /**
     * 今その色で選ばれている候補の範囲（直近 [WINNER_MAX_AGE] 秒以内）を、解析スレッドで読んで main へ返す。
     * 送信中でなければ null。コーンはほぼ毎コマ選ばれるので、押した瞬間のコマが欠けても拾えるよう少し幅を持たせる。
     */
    fun captureWinnerZone(color: Int, callback: (IgnoreZone?) -> Unit) {
        val token = generation
        try {
            executor.execute {
                val zone = if (running && token == generation)
                    core?.winnerZone(color, System.nanoTime() / 1e9, WINNER_MAX_AGE) else null
                main.post { callback(zone) }
            }
        } catch (_: java.util.concurrent.RejectedExecutionException) { main.post { callback(null) } }
    }

    /** プレイ範囲の学習を始める（解析スレッド上で）。送信中でなければ何もしない（終わりに null が返る）。 */
    fun startAreaLearning() {
        val token = generation
        try {
            executor.execute { if (running && token == generation) core?.startAreaLearning() }
        } catch (_: java.util.concurrent.RejectedExecutionException) { }
    }

    /** 学習を終えて結果を main へ返す。学習していなかった（送信停止・カメラ再起動）なら null。 */
    fun finishAreaLearning(callback: (NativeCore.AreaLearning?) -> Unit) {
        try {
            executor.execute {
                val result = core?.finishAreaLearning()
                main.post { callback(result) }
            }
        } catch (_: java.util.concurrent.RejectedExecutionException) { main.post { callback(null) } }
    }

    /**
     * 露出補正を変える（main から）。カメラの再起動なしで次のフレームから効く。0 の間は露出に触れない
     * （0 以外を設定した後に 0 へ戻すときだけ index 0 を書き込む）。0 以外では認識に入る画素が変わる試験設定。
     */
    fun setExposureBias(ev: Int) {
        if (!ExposureBias.valid(ev)) return
        exposureBiasEv = ev
        applyExposureBias("set")
    }

    private fun applyExposureBias(reason: String) {
        val ev = exposureBiasEv
        val camera = boundCamera
        if (camera == null) {
            exposureStatus = "露出補正: ${ExposureBias.label(ev)}" + if (ev != 0) "（送信開始時に適用）" else ""
            return
        }
        if (ev == 0 && !exposureTouched) {
            exposureStatus = "露出補正: ${ExposureBias.label(0)}"
            return
        }
        val state = camera.cameraInfo.exposureState
        val step = state.exposureCompensationStep
        val range = state.exposureCompensationRange
        val index = if (state.isExposureCompensationSupported)
            ExposureBias.index(ev, step.numerator, step.denominator, range.lower, range.upper) else null
        if (index == null) {
            exposureStatus = "露出補正: この端末は非対応（${ExposureBias.label(ev)} を適用できません）"
            Log.i(EXPOSURE_TAG, "ev=$ev reason=$reason unsupported range=$range step=$step")
            return
        }
        if (ev != 0) exposureTouched = true
        val future = camera.cameraControl.setExposureCompensationIndex(index)
        future.addListener({
            if (boundCamera !== camera) return@addListener
            val applied = try { future.get() } catch (e: Exception) { null }
            if (applied != null && ev == 0) exposureTouched = false
            val appliedEv = applied?.let { it.toDouble() * step.numerator / step.denominator }
            exposureStatus = if (applied == null) "露出補正: ${ExposureBias.label(ev)} を適用できませんでした"
                else String.format(java.util.Locale.US, "露出補正: %s（適用 %+.2f EV = index %d × %s、範囲 %d〜%d）",
                    ExposureBias.label(ev), appliedEv, applied, step, range.lower, range.upper)
            Log.i(EXPOSURE_TAG, "ev=$ev reason=$reason requested_index=$index applied_index=$applied " +
                "applied_ev=$appliedEv step=$step range=$range")
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
        boundCamera = null
        applyExposureBias("stop") // カメラが無いので表示だけ戻す（露出には触れない）
        analysis?.clearAnalyzer(); analysis = null
        provider?.unbindAll()
        executor.execute {
            expiry?.cancel(false); expiry = null
            core?.close(); core = null
        }
    }

    companion object {
        const val WINNER_MAX_AGE = 0.25
        const val EXPOSURE_TAG = "PhoneSaberExposure"
    }

    fun close() {
        stop()
        // Drain already-delivered analyzers (which close their ImageProxy) and native destruction.
        executor.shutdown()
    }
}
