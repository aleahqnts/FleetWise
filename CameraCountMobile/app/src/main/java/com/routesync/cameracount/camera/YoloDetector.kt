package com.routesync.cameracount.camera

import android.content.Context
import android.graphics.Bitmap
import android.graphics.RectF
import org.tensorflow.lite.Interpreter
import org.tensorflow.lite.gpu.CompatibilityList
import org.tensorflow.lite.gpu.GpuDelegate
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * YOLO11n person detector on LiteRT/TFLite. GPU delegate when the device supports it,
 * 4-thread CPU (XNNPACK) otherwise — emulators usually land on CPU.
 *
 * Model file: app/src/main/assets/yolo11n_float32.tflite (see assets/README.txt for the
 * one-line Ultralytics export). Missing model -> tryCreate returns null and the UI shows
 * instructions instead of crashing.
 *
 * Output contract (Ultralytics TFLite export): float32 [1, 84, N] — 4 box coords
 * (cx, cy, w, h, normalized) + 80 COCO class scores per anchor. Person = class 0.
 * Transposed [1, N, 84] exports are auto-detected.
 */
class YoloDetector private constructor(
    private val interpreter: Interpreter,
    val inputSize: Int,
    private val nchw: Boolean, // true = [1,3,s,s] channels-first (ONNX-style export)
    private val outShape: IntArray,
    val usingGpu: Boolean
) {
    /** Box normalized 0..1 in the letterboxed input-square space. */
    data class Det(val box: RectF, val score: Float)

    companion object {
        const val MODEL_ASSET = "yolo11n_float32.tflite"
        private const val CONF_THRESHOLD = 0.40f
        private const val IOU_THRESHOLD = 0.45f
        private const val PERSON_CLASS = 0

        fun tryCreate(context: Context): YoloDetector? = try {
            val bytes = context.assets.open(MODEL_ASSET).use { it.readBytes() }
            val model = ByteBuffer.allocateDirect(bytes.size).order(ByteOrder.nativeOrder())
            model.put(bytes)
            model.rewind()

            val options = Interpreter.Options()
            val compat = CompatibilityList()
            val gpu = compat.isDelegateSupportedOnThisDevice
            if (gpu) options.addDelegate(GpuDelegate(compat.bestOptionsForThisDevice))
            else options.setNumThreads(4)

            val itp = Interpreter(model, options)
            // NHWC [1,s,s,3] (standard TFLite) or NCHW [1,3,s,s] (ONNX-style) — detect.
            val inShape = itp.getInputTensor(0).shape()
            val nchw = inShape[1] == 3
            val size = if (nchw) inShape[2] else inShape[1]
            YoloDetector(itp, size, nchw, itp.getOutputTensor(0).shape(), gpu)
        } catch (e: Exception) {
            android.util.Log.w("YoloDetector", "model load failed: ${e.message}")
            null
        }
    }

    private val input: ByteBuffer =
        ByteBuffer.allocateDirect(inputSize * inputSize * 3 * 4).order(ByteOrder.nativeOrder())
    private val pixels = IntArray(inputSize * inputSize)
    // [1, attrs, anchors] regardless of export layout; normalized after read.
    private val transposed = outShape[1] > outShape[2] // [1, N, 84] instead of [1, 84, N]
    private val attrs = if (transposed) outShape[2] else outShape[1]
    private val anchors = if (transposed) outShape[1] else outShape[2]
    private val out = Array(1) { Array(outShape[1]) { FloatArray(outShape[2]) } }

    // close() can race an in-flight analyze on the camera executor; synchronized +
    // flag make the last frame drain safely instead of running on freed native memory.
    @Volatile
    private var closed = false

    /** [bmp] must already be [inputSize] x [inputSize] (letterboxed by the analyzer). */
    @Synchronized
    fun detect(bmp: Bitmap): List<Det> {
        if (closed) return emptyList()
        bmp.getPixels(pixels, 0, inputSize, 0, 0, inputSize, inputSize)
        input.rewind()
        if (nchw) {
            // channels-first: all R, then all G, then all B
            for (p in pixels) input.putFloat(((p shr 16) and 0xFF) / 255f)
            for (p in pixels) input.putFloat(((p shr 8) and 0xFF) / 255f)
            for (p in pixels) input.putFloat((p and 0xFF) / 255f)
        } else {
            // interleaved RGB per pixel
            for (p in pixels) {
                input.putFloat(((p shr 16) and 0xFF) / 255f)
                input.putFloat(((p shr 8) and 0xFF) / 255f)
                input.putFloat((p and 0xFF) / 255f)
            }
        }
        interpreter.run(input, out)

        fun v(attr: Int, i: Int) = if (transposed) out[0][i][attr] else out[0][attr][i]

        val raw = ArrayList<Det>()
        for (i in 0 until anchors) {
            val score = v(4 + PERSON_CLASS, i)
            if (score < CONF_THRESHOLD) continue
            var cx = v(0, i); var cy = v(1, i); var w = v(2, i); var h = v(3, i)
            // Some exports emit pixel-space boxes; normalize defensively.
            if (cx > 2f || cy > 2f) { cx /= inputSize; cy /= inputSize; w /= inputSize; h /= inputSize }
            raw.add(Det(RectF(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2), score))
        }
        return nms(raw)
    }

    private fun nms(dets: List<Det>): List<Det> {
        val sorted = dets.sortedByDescending { it.score }.toMutableList()
        val keep = ArrayList<Det>()
        while (sorted.isNotEmpty()) {
            val best = sorted.removeAt(0)
            keep.add(best)
            sorted.removeAll { iou(best.box, it.box) > IOU_THRESHOLD }
        }
        return keep
    }

    private fun iou(a: RectF, b: RectF): Float {
        val ix = maxOf(0f, minOf(a.right, b.right) - maxOf(a.left, b.left))
        val iy = maxOf(0f, minOf(a.bottom, b.bottom) - maxOf(a.top, b.top))
        val inter = ix * iy
        val union = a.width() * a.height() + b.width() * b.height() - inter
        return if (union <= 0f) 0f else inter / union
    }

    @Synchronized
    fun close() {
        if (closed) return
        closed = true
        interpreter.close()
    }
}
