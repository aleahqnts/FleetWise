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

        // Letterbox into the model square, preserving aspect.
        val s = detector.inputSize
        val scale = s.toFloat() / maxOf(upright.width, upright.height)
        val dw = upright.width * scale
        val dh = upright.height * scale
        val dx = (s - dw) / 2f
        val dy = (s - dh) / 2f
        val square = Bitmap.createBitmap(s, s, Bitmap.Config.ARGB_8888)
        Canvas(square).drawBitmap(upright, null, RectF(dx, dy, dx + dw, dy + dh), null)

        val dets = detector.detect(square).map { d ->
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
