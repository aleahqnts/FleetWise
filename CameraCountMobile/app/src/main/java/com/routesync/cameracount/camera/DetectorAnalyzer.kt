package com.routesync.cameracount.camera

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Matrix
import android.graphics.RectF
import android.os.SystemClock
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy

/**
 * Turns a CameraX frame into person detections for the overlay.
 *
 * Each frame is rotated upright, letterboxed into the model's input square, passed
 * through the detector, and the resulting boxes are mapped back to frame-normalized
 * coordinates, 0 to 1 of the upright camera frame.
 *
 * @param mirrored true for the front camera. PreviewView mirrors the preview but the
 *   analyzer frame arrives unmirrored, so the X axis is flipped here to put boxes on the
 *   people the driver sees.
 */
class DetectorAnalyzer(
    private val detector: YoloDetector,
    private val mirrored: Boolean,
    private val onError: (String) -> Unit = {},
    /**
     * Processes one frame in every N, where 1 means every frame.
     *
     * Heat is the main cause of failure on a mounted phone, and inference is the dominant
     * source of it. Dropping the inference rate under thermal pressure sheds most of the
     * load while the tracker's velocity prediction covers the skipped frames.
     */
    private val throttle: () -> Int = { 1 },
    /**
     * Fires for every frame the camera delivers, before throttling and before inference.
     * It means the session is alive and nothing more.
     *
     * [onResult] cannot answer that question, because the throttle skips it and so does
     * any inference failure, which makes a broken detector on a healthy camera look
     * identical to a dead camera. Only one of those is repaired by rebinding, so the
     * watchdog needs to tell them apart.
     */
    private val onFrame: () -> Unit = {},
    private val onResult: (dets: List<YoloDetector.Det>, frameW: Int, frameH: Int, inferMs: Long) -> Unit
) : ImageAnalysis.Analyzer {

    private var frameNo = 0L

    /**
     * One-shot frame grab, used to snapshot the doorway for a remote calibration without
     * opening a second camera session. Consumed and cleared on the next analyzed frame.
     *
     * The frame arrives in display space, upright and mirrored for the front lens, which
     * is the space the counting line's normalized coordinates use.
     */
    @Volatile var frameTap: ((Bitmap) -> Unit)? = null

    // Reused across frames, which is safe because the analyzer is single-threaded.
    // Allocating a model-input bitmap per frame would produce constant garbage collection
    // and additional heat.
    private var square: Bitmap? = null
    private var squareCanvas: Canvas? = null

    override fun analyze(image: ImageProxy) {
        runCatching { onFrame() } // liveness ping: must not be able to skip the frame below
        try {
            val n = throttle().coerceAtLeast(1)
            if (frameNo++ % n == 0L) analyzeInner(image)
        } catch (e: Exception) {
            // A dropped frame must never take the app down. Teardown races and memory
            // spikes both surface here.
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

        // Hand the snapshot tap a detached copy in display space, then clear it.
        frameTap?.let { tap ->
            frameTap = null
            val out = if (mirrored) {
                val m = Matrix().apply { postScale(-1f, 1f) }
                Bitmap.createBitmap(upright, 0, 0, upright.width, upright.height, m, true)
            } else upright.copy(Bitmap.Config.ARGB_8888, false)
            tap(out)
        }

        // Letterbox into the model square, preserving aspect ratio. The square buffer is
        // reused between frames, so the borders are cleared to avoid ghosting from the
        // previous frame.
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
            // Input square (0 to 1) to pixels, remove the letterbox, then normalize
            // against the frame.
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
