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
 * Device-local settings.
 *
 * The phone is a fixed fixture on one bus, so it binds to a vehicle once. The passcode
 * guards against the phone being re-pointed at a different bus.
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
        // Default line runs vertically down the middle of the frame.
        const val DEF_AX = 0.5f; const val DEF_AY = 0.05f
        const val DEF_BX = 0.5f; const val DEF_BY = 0.95f
        const val DEF_INWARD_SIGN = 1
    }

    val vehicleId: Flow<String?> = context.dataStore.data.map { it[VEHICLE_ID] }
    val plate: Flow<String?> = context.dataStore.data.map { it[PLATE] }

    /**
     * Stable per-installation identifier used to claim a trip.
     *
     * The first camera phone to claim a trip writes this to `trips.counter_device_id`. A
     * second phone bound to the same bus sees the claim and stands by rather than
     * counting the same passengers again.
     */
    suspend fun deviceId(): String {
        context.dataStore.data.first()[DEVICE_ID]?.let { return it }
        val id = "cam-" + java.util.UUID.randomUUID().toString().take(8)
        context.dataStore.edit { it[DEVICE_ID] = id }
        return id
    }

    /**
     * Device JWT minted at bind time by the device-token edge function, valid for 365 days.
     *
     * It survives an unbind because it carries only the device identifier. Vehicle scope
     * comes from the database join, so re-binding to another bus reuses the same token.
     */
    suspend fun deviceJwt(): String? = context.dataStore.data.first()[DEVICE_JWT]

    suspend fun saveDeviceJwt(jwt: String) {
        context.dataStore.edit { it[DEVICE_JWT] = jwt }
    }

    /**
     * Which camera faces the doorway, decided by how the phone is mounted and changed
     * from the calibrate screen.
     *
     * The back camera often has a 0.6x ultrawide lens, which fits the whole approach path
     * in frame at dashboard distance.
     */
    val useBackCamera: Flow<Boolean> = context.dataStore.data.map { it[USE_BACK_CAMERA] ?: false }

    suspend fun saveUseBackCamera(v: Boolean) {
        context.dataStore.edit { it[USE_BACK_CAMERA] = v }
    }

    /** Counting-line calibration: two endpoints at any angle, plus the boarding side. */
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

    // The `device_config` row is the source of truth and DataStore is the offline cache.
    // CONFIG_VERSION is the version this device last applied, or authored when the
    // calibration was made on the phone.

    suspend fun configVersion(): Int = context.dataStore.data.first()[CONFIG_VERSION] ?: 0

    suspend fun saveConfigVersion(v: Int) {
        context.dataStore.edit { it[CONFIG_VERSION] = v }
    }

    /**
     * Applies a newer remote configuration in a single edit.
     *
     * Line, lens and version land together, so a crash part-way through cannot leave a
     * cache that claims the new version while still holding the old line.
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
     * Trip and count, persisted on every change so a dead zone, a process kill or a
     * reboot mid-trip does not lose passengers.
     *
     * On restart the count resumes at the higher of the saved and stored values when the
     * same trip is still active. A different trip discards it.
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
            // The passcode only exists to gate the unbind that just happened. Keeping
            // it would leave the fleet secret on a phone bound to nothing.
            it.remove(PASSCODE)
        }
    }
}
