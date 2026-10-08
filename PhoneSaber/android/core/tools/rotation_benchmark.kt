package jp.phonesaber.sender

import java.io.File
import java.nio.ByteBuffer
import java.util.Locale

// JVM warm-up・全画素回転・JNI・core・結果生成を含む。カメラ取得/UDP/デコードは含まない。
fun main(args: Array<String>) {
    val width = args[1].toInt(); val height = args[2].toInt(); val stride = args[3].toInt()
    val data = File(args[0]).readBytes()
    val input = ByteBuffer.allocateDirect(data.size).put(data).apply { flip() }
    val rotation = RotationHelper()
    val old = NativeCore(MirrorSettings())
    val native = NativeCore(MirrorSettings())
    val probe = NativeRotationProbe.create()
    var checksum = 0
    try {
        for (angle in intArrayOf(0, 90, 180, 270)) {
            var tick = 0
            fun frame(before: Boolean, rotationOnly: Boolean) {
                if (rotationOnly) {
                    val buffer = if (before) rotation.orient(input, width, height, stride, 4, angle).buffer
                    else NativeRotationProbe.rotate(probe, input, 0, data.size, width, height, stride, angle)
                    checksum += buffer.get(0).toInt()
                } else {
                    val results = if (before) old.process(rotation.orient(input, width, height, stride, 4, angle),
                        (++tick) / 60.0, 145, 25)
                    else native.processCamera(input, width, height, stride, 4, angle, (++tick) / 60.0, 145, 25)
                    checksum += results.size
                }
            }
            for (rotationOnly in listOf(true, false)) {
                repeat(400) { frame(it % 2 == 0, rotationOnly) }
                val medians = Array(2) { ArrayList<Double>() }
                repeat(5) { pair ->
                    for (index in if (pair % 2 == 0) intArrayOf(0, 1) else intArrayOf(1, 0)) {
                        val times = DoubleArray(100) {
                            val start = System.nanoTime()
                            frame(index == 0, rotationOnly)
                            (System.nanoTime() - start) / 1e6
                        }
                        times.sort()
                        medians[index].add(times[50])
                    }
                }
                val before = medians[0].sorted()[2]; val after = medians[1].sorted()[2]
                println(String.format(Locale.US, "angle=%d %s before_ms=%.6f after_ms=%.6f reduction=%.1f%%",
                    angle, if (rotationOnly) "rotation" else "rotation+jni+core", before, after, (before-after)*100/before))
            }
        }
    } finally { NativeRotationProbe.destroy(probe); old.close(); native.close() }
    println("checksum=$checksum")
}
