package com.routesync.cameracount.data

import android.content.Context
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.floatPreferencesKey
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

private val Context.dataStore by preferencesDataStore(name = "cameracount")

/**
 * Device-local settings. The dashboard phone is a fixed fixture per bus, so it binds
 * to a vehicle_id once; the passcode guards against re-pointing the phone at another bus.
 */
class Prefs(private val context: Context) {

    companion object {
        private val VEHICLE_ID = stringPreferencesKey("vehicle_id")
        private val PLATE = stringPreferencesKey("plate")
        private val PASSCODE = stringPreferencesKey("passcode")
        private val DEVICE_ID = stringPreferencesKey("device_id")
        private val LINE_AX = floatPreferencesKey("line_ax")
        private val LINE_AY = floatPreferencesKey("line_ay")
        private val LINE_BX = floatPreferencesKey("line_bx")
        private val LINE_BY = floatPreferencesKey("line_by")
        private val LINE_INWARD_SIGN = intPreferencesKey("line_inward_sign")
        private val PENDING_TRIP_ID = stringPreferencesKey("pending_trip_id")
        private val PENDING_COUNT = intPreferencesKey("pending_count")
        private val DEVICE_JWT = stringPreferencesKey("device_jwt")
        private val USE_BACK_CAMERA = androidx.datastore.preferences.core.booleanPreferencesKey("use_back_camera")
        private val CONFIG_VERSION = intPreferencesKey("config_version")
        // Default = vertical line down the middle (same as before, just as two endpoints).
        const val DEF_AX = 0.5f; const val DEF_AY = 0.05f
        const val DEF_BX = 0.5f; const val DEF_BY = 0.95f
        const val DEF_INWARD_SIGN = 1
    }

    val vehicleId: Flow<String?> = context.dataStore.data.map { it[VEHICLE_ID] }
    val plate: Flow<String?> = context.dataStore.data.map { it[PLATE] }

    /**
     * Stable per-install ID for the trip claim (double-link safeguard): the first camera
     * phone to claim a trip writes this onto trips.counter_device_id; a second phone
     * bound to the same bus sees the claim and stands by instead of double counting.
     */
    suspend fun deviceId(): String {
        context.dataStore.data.first()[DEVICE_ID]?.let { return it }
        val id = "cam-" + java.util.UUID.randomUUID().toString().take(8)
        context.dataStore.edit { it[DEVICE_ID] = id }
        return id
    }

    /**
     * Phase 7: app_camera JWT minted by the device-token edge fn at bind time (365d).
     * Survives unbind — it carries only device_id; vehicle scope is the DB join, so a
     * re-bind to another bus reuses the same token.
     */
    suspend fun deviceJwt(): String? = context.dataStore.data.first()[DEVICE_JWT]

    suspend fun saveDeviceJwt(jwt: String) {
        context.dataStore.edit { it[DEVICE_JWT] = jwt }
    }

    /**
     * Which camera faces the doorway. Back camera often has a 0.6x ultrawide -> whole
     * approach path in frame at dashboard distance. Mount decides; toggle in Calibrate.
     */
    val useBackCamera: Flow<Boolean> = context.dataStore.data.map { it[USE_BACK_CAMERA] ?: false }

    suspend fun saveUseBackCamera(v: Boolean) {
        context.dataStore.edit { it[USE_BACK_CAMERA] = v }
    }

    /** Counting-line calibration (Phase 5): two endpoints (any angle) + boarding side. */
    data class LineCalibration(
        val ax: Float, val ay: Float, val bx: Float, val by: Float, val inwardSign: Int
    )

    val lineCalibration: Flow<LineCalibration> = context.dataStore.data.map {
        LineCalibration(
            it[LINE_AX] ?: DEF_AX, it[LINE_AY] ?: DEF_AY,
            it[LINE_BX] ?: DEF_BX, it[LINE_BY] ?: DEF_BY,
            it[LINE_INWARD_SIGN] ?: DEF_INWARD_SIGN
        )
    }

    suspend fun saveLine(ax: Float, ay: Float, bx: Float, by: Float, inwardSign: Int) {
        context.dataStore.edit {
            it[LINE_AX] = ax; it[LINE_AY] = ay
            it[LINE_BX] = bx; it[LINE_BY] = by
            it[LINE_INWARD_SIGN] = inwardSign
        }
    }

    // ------------------------------------------------------------------
    // Phase 8a: DB `device_config` row is the source of truth; DataStore is
    // the offline cache. CONFIG_VERSION = the version this device last
    // applied (or authored, on a local calibrate).
    // ------------------------------------------------------------------

    suspend fun configVersion(): Int = context.dataStore.data.first()[CONFIG_VERSION] ?: 0

    /** Local calibrate authors a NEW version (version+1) that gets pushed up. */
    suspend fun bumpConfigVersion(): Int {
        val v = configVersion() + 1
        context.dataStore.edit { it[CONFIG_VERSION] = v }
        return v
    }

    /**
     * Apply a newer remote config in ONE atomic edit — line, lens, and version
     * land together so a mid-apply crash can't leave a half-applied cache that
     * still claims the new version.
     */
    suspend fun applyRemoteConfig(
        ax: Float, ay: Float, bx: Float, by: Float,
        inwardSign: Int, useBack: Boolean, version: Int
    ) {
        context.dataStore.edit {
            it[LINE_AX] = ax; it[LINE_AY] = ay
            it[LINE_BX] = bx; it[LINE_BY] = by
            it[LINE_INWARD_SIGN] = inwardSign
            it[USE_BACK_CAMERA] = useBack
            it[CONFIG_VERSION] = version
        }
    }

    /**
     * Phase 6 durable count: (tripId, count) persisted on every change so a dead zone,
     * app kill, or reboot mid-trip never loses passengers. On restart, if the SAME trip
     * is still Active the count resumes at max(saved, db); a different trip discards it.
     */
    data class PendingCount(val tripId: String, val count: Int)

    suspend fun pendingCount(): PendingCount? {
        val d = context.dataStore.data.first()
        val t = d[PENDING_TRIP_ID] ?: return null
        return PendingCount(t, d[PENDING_COUNT] ?: 0)
    }

    suspend fun savePendingCount(tripId: String, count: Int) {
        context.dataStore.edit {
            it[PENDING_TRIP_ID] = tripId
            it[PENDING_COUNT] = count
        }
    }

    suspend fun clearPendingCount() {
        context.dataStore.edit {
            it.remove(PENDING_TRIP_ID)
            it.remove(PENDING_COUNT)
        }
    }

    suspend fun bind(vehicleId: String, passcode: String, plate: String) {
        context.dataStore.edit {
            it[VEHICLE_ID] = vehicleId
            it[PLATE] = plate
            it[PASSCODE] = passcode
        }
    }

    suspend fun checkPasscode(input: String): Boolean =
        context.dataStore.data.first()[PASSCODE] == input

    suspend fun unbind() {
        context.dataStore.edit {
            it.remove(VEHICLE_ID)
            it.remove(PLATE)
        }
    }
}
