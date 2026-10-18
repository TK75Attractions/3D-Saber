package jp.phonesaber.sender

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.view.View

/**
 * プレビュー（PreviewView FIT_CENTER）の上に無視範囲を枠で描く。
 * プレビューと解析は同じ解像度選択・同じ縦向きなので、解析フレームの縦横比で表示位置を求める。
 */
class IgnoreZoneOverlay(context: Context) : View(context) {
    private var zones = IgnoreZones.NONE
    private var frame: Pair<Int, Int>? = null
    private val density = resources.displayMetrics.density
    private val redPaint = paint(Color.rgb(255, 80, 80))
    private val bluePaint = paint(Color.rgb(80, 160, 255))

    private fun paint(color: Int) = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        this.color = color; style = Paint.Style.STROKE; strokeWidth = 2.5f * density
        pathEffect = android.graphics.DashPathEffect(floatArrayOf(8 * density, 5 * density), 0f)
    }

    fun update(zones: IgnoreZones, frame: Pair<Int, Int>?) {
        if (zones == this.zones && frame == this.frame) return
        this.zones = zones; this.frame = frame
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (zones.isEmpty) return
        // 寸法がまだ不明なら、この端末の既定（VGA 以下の 3:4 縦）とみなす。
        val (frameWidth, frameHeight) = frame ?: (480 to 640)
        val fit = Fit.of(width.toDouble(), height.toDouble(), frameWidth.toDouble(), frameHeight.toDouble())
        for ((zones, paint) in listOf(zones.red to redPaint, zones.blue to bluePaint)) for (z in zones) {
            val left = fit.x(z.minX.coerceIn(0.0, 1.0)); val right = fit.x(z.maxX.coerceIn(0.0, 1.0))
            val top = fit.y(z.minY.coerceIn(0.0, 1.0)); val bottom = fit.y(z.maxY.coerceIn(0.0, 1.0))
            canvas.drawRect(left.toFloat(), top.toFloat(), right.toFloat(), bottom.toFloat(), paint)
        }
    }

    /** 0..1 の解析フレーム座標 → View 上の px（FIT_CENTER）。 */
    data class Fit(val left: Double, val top: Double, val width: Double, val height: Double) {
        fun x(normalized: Double) = left + normalized * width
        fun y(normalized: Double) = top + normalized * height

        companion object {
            fun of(viewWidth: Double, viewHeight: Double, frameWidth: Double, frameHeight: Double): Fit {
                if (viewWidth <= 0 || viewHeight <= 0 || frameWidth <= 0 || frameHeight <= 0)
                    return Fit(0.0, 0.0, maxOf(viewWidth, 0.0), maxOf(viewHeight, 0.0))
                val scale = minOf(viewWidth / frameWidth, viewHeight / frameHeight)
                val w = frameWidth * scale; val h = frameHeight * scale
                return Fit((viewWidth - w) / 2, (viewHeight - h) / 2, w, h)
            }
        }
    }
}
