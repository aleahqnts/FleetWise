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
 * YOLO11n person detector running on TFLite, with the GPU delegate where it works and
 * four CPU threads otherwise.
 *
 * The model lives at `app/src/main/assets/yolo11n_float32.tflite`; `assets/README.txt`
 * has the export command. A missing model makes [tryCreate] return null so the UI can
 * show instructions rather than crash.
 *
 * Output from an Ultralytics TFLite export is float32 shaped [1, 84, N]: four normalized
 * box coordinates as centre x, centre y, width and height, then 80 COCO class scores per
 * anchor, of which person is class 0. Transposed [1, N, 84] exports are detected and
 * handled.
 */
class YoloDetector private constructor(
    private val interpreter: Interpreter,
    val inputSize: Int,
    private val nchw: Boolean, // true = [1,3,s,s] channels-first (ONNX-style export)
    private val outShape: IntArray,
    val usingGpu: Boolean,
    // Held only so close() can free it. The delegate owns native GL resources that the
    // interpreter does not release.
    private val gpuDelegate: GpuDelegate?
) {
    /** Box normalized 0..1 in the letterboxed input-square space. */
    data class Det(val box: RectF, val score: Float)

    companion object {
        const val MODEL_ASSET = "yolo11n_float32.tflite"
        /** Confidence required to display a box or start a new track. The detector emits
         *  down to CONF_THRESHOLD so the tracker's low-confidence rescue stage can keep
         *  occluded tracks alive. */
        const val HIGH_CONF = 0.40f
        private const val CONF_THRESHOLD = 0.25f
        // Non-maximum suppression keeps a box unless it overlaps a higher-scoring one by
        // more than this. Set high so two people standing close stay as two boxes, and
        // only near-identical duplicates of one person are merged.
        private const val IOU_THRESHOLD = 0.55f
        private const val PERSON_CLASS = 0

        /**
         * Loads the model, preferring the GPU delegate.
         *
         * The delegate roughly halves inference time. On CPU the model runs at about 80ms
         * a frame, and sustained four-thread inference is the largest heat source in a
         * closed bus, which trips the thermal guard and reduces the frame rate further.
         *
         * `CompatibilityList` is an allowlist shipped inside TFLite rather than a probe of
         * the device, and it is conservative enough to refuse hardware that runs the
         * delegate correctly. It is therefore treated as a hint about which options to
         * use, not as a veto: the interpreter is built with the delegate and falls back to
         * CPU only if that construction fails.
         */
        fun tryCreate(context: Context): YoloDetector? = try {
            val bytes = context.assets.open(MODEL_ASSET).use { it.readBytes() }
            val model = ByteBuffer.allocateDirect(bytes.size).order(ByteOrder.nativeOrder())
            model.put(bytes)
            model.rewind()

            var delegate: GpuDelegate? = runCatching {
                val compat = CompatibilityList()
                if (compat.isDelegateSupportedOnThisDevice) GpuDelegate(compat.bestOptionsForThisDevice)
                else GpuDelegate()
            }.getOrNull()

            var itp = delegate?.let { d ->
                runCatching { Interpreter(model, Interpreter.Options().addDelegate(d)) }
                    .onFailure {
                        // An unsupported operation, a driver refusal, or no GL context. Not fatal.
                        android.util.Log.w("YoloDetector", "GPU delegate unusable, using CPU: ${it.message}")
                        runCatching { d.close() }
                        delegate = null
                        model.rewind() // the failed Interpreter consumed the buffer
                    }
                    .getOrNull()
            }
            if (itp == null) itp = Interpreter(model, Interpreter.Options().setNumThreads(4))

            // Either NHWC [1,s,s,3], standard for TFLite, or NCHW [1,3,s,s] from an
            // ONNX-style export.
            val inShape = itp.getInputTensor(0).shape()
            val nchw = inShape[1] == 3
            val size = if (nchw) inShape[2] else inShape[1]
            android.util.Log.i(
                "YoloDetector", "input ${size}x$size, ${if (delegate != null) "GPU" else "CPU"}"
            )
            YoloDetector(itp, size, nchw, itp.getOutputTensor(0).shape(), delegate != null, delegate)
        } catch (e: Exception) {
            android.util.Log.w("YoloDetector", "model load failed: ${e.message}")
            null
        }
    }

    private val input: ByteBuffer =
        ByteBuffer.allocateDirect(inputSize * inputSize * 3 * 4).order(ByteOrder.nativeOrder())
    private val pixels = IntArray(inputSize * inputSize)
    // Read as [1, attrs, anchors] whichever layout the export used.
    private val transposed = outShape[1] > outShape[2] // [1, N, 84] instead of [1, 84, N]
    private val attrs = if (transposed) outShape[2] else outShape[1]
    private val anchors = if (transposed) outShape[1] else outShape[2]
    private val out = Array(1) { Array(outShape[1]) { FloatArray(outShape[2]) } }

    // close() can race a frame still being analyzed on the camera executor. The lock and
    // this flag let that frame drain instead of running against freed native memory.
    @Volatile
    private var closed = false

    /** [bmp] must already be [inputSize] square, letterboxed by the analyzer. */
    @Synchronized
    fun detect(bmp: Bitmap): List<Det> {
        if (closed) return emptyList()
        bmp.getPixels(pixels, 0, inputSize, 0, 0, inputSize, inputSize)
        input.rewind()
        if (nchw) {
            // Channels first: all red, then all green, then all blue.
            for (p in pixels) input.putFloat(((p shr 16) and 0xFF) / 255f)
            for (p in pixels) input.putFloat(((p shr 8) and 0xFF) / 255f)
            for (p in pixels) input.putFloat((p and 0xFF) / 255f)
        } else {
            // Interleaved red, green and blue per pixel.
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
            // Some exports emit boxes in pixel space rather than normalized.
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
        gpuDelegate?.close()
    }
}
