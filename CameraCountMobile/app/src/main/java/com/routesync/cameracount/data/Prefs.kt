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
        private val PASSCODE = stringPreferencesKey("passcode")
        private val LINE_AX = floatPreferencesKey("line_ax")
        private val LINE_AY = floatPreferencesKey("line_ay")
        private val LINE_BX = floatPreferencesKey("line_bx")
        private val LINE_BY = floatPreferencesKey("line_by")
        private val LINE_INWARD_SIGN = intPreferencesKey("line_inward_sign")
        // Default = vertical line down the middle (same as before, just as two endpoints).
        const val DEF_AX = 0.5f; const val DEF_AY = 0.05f
        const val DEF_BX = 0.5f; const val DEF_BY = 0.95f
        const val DEF_INWARD_SIGN = 1
    }

    val vehicleId: Flow<String?> = context.dataStore.data.map { it[VEHICLE_ID] }

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

    suspend fun bind(vehicleId: String, passcode: String) {
        context.dataStore.edit {
            it[VEHICLE_ID] = vehicleId
            it[PASSCODE] = passcode
        }
    }

    suspend fun checkPasscode(input: String): Boolean =
        context.dataStore.data.first()[PASSCODE] == input

    suspend fun unbind() {
        context.dataStore.edit {
            it.remove(VEHICLE_ID)
        }
    }
}
