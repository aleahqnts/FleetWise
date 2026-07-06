package com.routesync.cameracount.camera

import android.hardware.camera2.CameraCharacteristics
import androidx.camera.camera2.interop.Camera2CameraInfo
import androidx.camera.core.CameraInfo
import androidx.camera.core.CameraSelector
import kotlin.math.atan2
import kotlin.math.hypot

/**
 * Some phones don't expose the 0.6x ultrawide as a zoom range on the logical back camera
 * (minZoomRatio stays 1.0) — the ultrawide is a separate physical sensor. When the OEM
 * DOES surface it as its own CameraInfo, this finds it: compute each back camera's
 * diagonal field-of-view from sensor size + focal length and pick the WIDEST.
 *
 * If nothing wider than the default is exposed (OEM locks physical IDs to system apps),
 * returns the plain back selector + its FOV so the UI can say "wide lens unavailable".
 */
object LensPicker {

    data class Pick(val selector: CameraSelector, val fovDegrees: Int, val isWide: Boolean)

    /** Widest FOV (deg, rounded) among cameras of the given facing; 0 if unreadable. */
    fun fovDegrees(cameraInfos: List<CameraInfo>, facing: Int): Int =
        cameraInfos.filter { it.lensFacing == facing }
            .mapNotNull { fovOf(it) }
            .maxOrNull()?.toInt() ?: 0

    /** Diagonal FOV in degrees, or null if the characteristics aren't readable. */
    private fun fovOf(info: CameraInfo): Float? = try {
        val c = Camera2CameraInfo.from(info)
        val size = c.getCameraCharacteristic(CameraCharacteristics.SENSOR_INFO_PHYSICAL_SIZE)
        val focals = c.getCameraCharacteristic(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS)
        if (size == null || focals == null || focals.isEmpty()) null
        else {
            val diag = hypot(size.width, size.height)
            val f = focals.min() // shortest focal length = widest view
            Math.toDegrees(2.0 * atan2((diag / 2.0), f.toDouble())).toFloat()
        }
    } catch (_: Exception) { null }

    /**
     * @param cameraInfos provider.availableCameraInfos
     * @return widest back-facing lens as a bindable CameraSelector + its FOV.
     */
    fun widestBack(cameraInfos: List<CameraInfo>): Pick {
        val backs = cameraInfos.filter {
            it.lensFacing == CameraSelector.LENS_FACING_BACK
        }
        if (backs.isEmpty()) return Pick(CameraSelector.DEFAULT_BACK_CAMERA, 0, false)

        val scored = backs.map { it to (fovOf(it) ?: 0f) }
        val widest = scored.maxByOrNull { it.second }!!
        val defaultFov = fovOf(
            backs.firstOrNull() ?: backs[0]
        ) ?: 0f

        // Build a selector that pins CameraX to exactly this CameraInfo.
        val id = Camera2CameraInfo.from(widest.first).cameraId
        val selector = CameraSelector.Builder()
            .requireLensFacing(CameraSelector.LENS_FACING_BACK)
            .addCameraFilter { infos ->
                infos.filter { Camera2CameraInfo.from(it).cameraId == id }
            }
            .build()

        // "wide" = meaningfully wider than the default back cam (ultrawide territory).
        val isWide = widest.second >= defaultFov + 15f && widest.second > 85f
        return Pick(selector, widest.second.toInt(), isWide)
    }
}
