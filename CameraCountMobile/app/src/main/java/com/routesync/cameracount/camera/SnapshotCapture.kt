package com.routesync.cameracount.camera

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Matrix
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.LifecycleRegistry
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.util.concurrent.Executors
import kotlin.coroutines.resume

/**
 * Phase 8c maintenance-wake: one still frame WITHOUT counting, for remote calibration.
 * Used while the app is Waiting (no trip): binds the camera to a throwaway lifecycle,
 * grabs one frame after a short AE warm-up, tears everything down.
 *
 * The bitmap is produced in DISPLAY space — upright, and mirrored for the front lens —
 * because that is the space the counting line's normalized coords live in (the preview
 * is mirrored for front, and DetectorAnalyzer flips its boxes to match). A remote
 * editor dragging a line on this image maps 1:1 onto the camera's line. Back lens also
 * locks the same ultrawide min-zoom counting uses, so the snapshot FOV == counting FOV.
 */
object SnapshotCapture {

    /** Null on any failure (camera busy, permission gone, timeout) — caller reports idle. */
    suspend fun captureOnce(context: Context, useBack: Boolean): Bitmap? = withContext(Dispatchers.Main) {
        val provider = awaitProvider(context) ?: return@withContext null
        val owner = OneShotLifecycle()
        val analysis = ImageAnalysis.Builder()
            .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
            .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
            .build()
        val executor = Executors.newSingleThreadExecutor()
        try {
            // Same lens choice as counting (CameraScreen): front default, or widest back.
            val wantFront = !useBack
            val haveFront = provider.hasCamera(CameraSelector.DEFAULT_FRONT_CAMERA)
            val haveBack = provider.hasCamera(CameraSelector.DEFAULT_BACK_CAMERA)
            val front = if (wantFront) haveFront else !haveBack
            val backPick = if (!front) LensPicker.widestBack(provider.availableCameraInfos) else null
            val selector = if (front) CameraSelector.DEFAULT_FRONT_CAMERA else backPick!!.selector

            withTimeoutOrNull(10_000) {
                suspendCancellableCoroutine { cont ->
                    var frames = 0
                    analysis.setAnalyzer(executor) { image ->
                        try {
                            frames++
                            // Skip warm-up frames: the first few are dark while AE settles.
                            if (frames >= 5 && cont.isActive) {
                                val rotation = image.imageInfo.rotationDegrees
                                val raw = image.toBitmap()
                                val m = Matrix()
                                if (rotation != 0) m.postRotate(rotation.toFloat())
                                if (front) m.postScale(-1f, 1f) // display space (mirrored preview)
                                val out = if (!m.isIdentity)
                                    Bitmap.createBitmap(raw, 0, 0, raw.width, raw.height, m, true)
                                else raw.copy(Bitmap.Config.ARGB_8888, false)
                                cont.resume(out)
                            }
                        } finally {
                            runCatching { image.close() }
                        }
                    }
                    owner.start()
                    val cam = provider.bindToLifecycle(owner, selector, analysis)
                    if (!front) {
                        val minZoom = cam.cameraInfo.zoomState.value?.minZoomRatio ?: 1f
                        if (minZoom < 1f) cam.cameraControl.setZoomRatio(minZoom)
                    }
                }
            }
        } catch (_: Exception) {
            null // camera owned by another session (calibrate open), permission revoked, ...
        } finally {
            runCatching { provider.unbind(analysis) }
            owner.stop()
            executor.shutdown()
        }
    }

    private suspend fun awaitProvider(context: Context): ProcessCameraProvider? =
        runCatching {
            suspendCancellableCoroutine<ProcessCameraProvider> { cont ->
                val f = ProcessCameraProvider.getInstance(context)
                f.addListener({
                    runCatching { f.get() }
                        .onSuccess { if (cont.isActive) cont.resume(it) }
                        .onFailure { if (cont.isActive) cont.cancel(it) }
                }, ContextCompat.getMainExecutor(context))
            }
        }.getOrNull()

    /** Minimal LifecycleOwner so CameraX can bind without any UI on screen. */
    private class OneShotLifecycle : LifecycleOwner {
        private val reg = LifecycleRegistry(this)
        override val lifecycle: Lifecycle get() = reg
        fun start() { reg.currentState = Lifecycle.State.RESUMED }
        fun stop() { reg.currentState = Lifecycle.State.DESTROYED }
    }
}
