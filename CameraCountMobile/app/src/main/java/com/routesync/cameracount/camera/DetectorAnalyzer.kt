package com.routesync.cameracount.camera

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Matrix
import android.graphics.RectF
import android.os.SystemClock
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy

/**
 * CameraX frame -> rotate -> letterbox to the model's input square -> YOLO -> map boxes
 * back to FRAME-normalized coords (0..1 of the upright camera frame) for the overlay.
 *
 * [mirrored] = front camera: PreviewView mirrors the preview, the analyzer frame is not —
 * flip X here so boxes land on the people the driver actually sees.
 */
class DetectorAnalyzer(
    private val detector: YoloDetector,
    private val mirrored: Boolean,
    private val onError: (String) -> Unit = {},
    /**
     * Phase 6 thermal guard: process 1 of every N frames (1 = every frame). Dashboard
     * heat is the #1 real failure; dropping inference fps under SEVERE/CRITICAL thermal
     * sheds most of the CPU load while the tracker's velocity prediction bridges the gap.
     */
    private val throttle: () -> Int = { 1 },
    private val onResult: (dets: List<YoloDetector.Det>, frameW: Int, frameH: Int, inferMs: Long) -> Unit
) : ImageAnalysis.Analyzer {

    private var frameNo = 0L

    /**
     * Phase 8c: one-shot frame grab while counting (snapshot for remote calibration
     * without touching the live camera session). Consumed and cleared on the next
     * analyzed frame. Receives the frame in DISPLAY space (upright, mirrored for the
     * front lens) — the space the line's normalized coords live in.
     */
    @Volatile var frameTap: ((Bitmap) -> Unit)? = null

    // Reused across frames (analyzer is single-threaded): allocating a fresh model-input
    // bitmap per frame = constant GC churn + heat on a CPU-inference phone.
    private var square: Bitmap? = null
    private var squareCanvas: Canvas? = null

    override fun analyze(image: ImageProxy) {
        try {
            val n = throttle().coerceAtLeast(1)
            if (frameNo++ % n == 0L) analyzeInner(image)
        } catch (e: Exception) {
            // A dropped frame must never take the app down (teardown races, OOM spikes).
            android.util.Log.w("DetectorAnalyzer", "frame skipped", e)
            onError("${e.javaClass.simpleName}: ${e.message}")
        } finally {
            runCatching { image.close() }
        }
    }

    private fun analyzeInner(image: ImageProxy) {
        val t0 = SystemClock.elapsedRealtime()
        val rotation = image.imageInfo.rotationDegrees
        val bmp = image.toBitmap()

        val upright = if (rotation != 0) {
            val m = Matrix().apply { postRotate(rotation.toFloat()) }
            Bitmap.createBitmap(bmp, 0, 0, bmp.width, bmp.height, m, true)
        } else bmp

        // Phase 8c snapshot tap: hand out a detached copy in display space, then clear.
        frameTap?.let { tap ->
            frameTap = null
            val out = if (mirrored) {
                val m = Matrix().apply { postScale(-1f, 1f) }
                Bitmap.createBitmap(upright, 0, 0, upright.width, upright.height, m, true)
            } else upright.copy(Bitmap.Config.ARGB_8888, false)
            tap(out)
        }

        // Letterbox into the model square, preserving aspect. The square buffer is
        // reused frame-to-frame; borders are cleared so no previous-frame ghosting.
        val s = detector.inputSize
        val scale = s.toFloat() / maxOf(upright.width, upright.height)
        val dw = upright.width * scale
        val dh = upright.height * scale
        val dx = (s - dw) / 2f
        val dy = (s - dh) / 2f
        val sq = square ?: Bitmap.createBitmap(s, s, Bitmap.Config.ARGB_8888).also {
            square = it
            squareCanvas = Canvas(it)
        }
        squareCanvas!!.drawColor(android.graphics.Color.BLACK)
        squareCanvas!!.drawBitmap(upright, null, RectF(dx, dy, dx + dw, dy + dh), null)

        val dets = detector.detect(sq).map { d ->
            // input-square (0..1) -> pixels -> strip letterbox -> frame-normalized.
            var l = (d.box.left * s - dx) / dw
            var t = (d.box.top * s - dy) / dh
            var r = (d.box.right * s - dx) / dw
            var b = (d.box.bottom * s - dy) / dh
            if (mirrored) {
                val nl = 1f - r
                r = 1f - l
                l = nl
            }
            YoloDetector.Det(
                RectF(l.coerceIn(0f, 1f), t.coerceIn(0f, 1f), r.coerceIn(0f, 1f), b.coerceIn(0f, 1f)),
                d.score
            )
        }
        onResult(dets, upright.width, upright.height, SystemClock.elapsedRealtime() - t0)
    }
}
