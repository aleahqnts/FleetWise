package com.routesync.cameracount.data

import android.content.Context
import androidx.datastore.preferences.core.edit
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
    }

    val vehicleId: Flow<String?> = context.dataStore.data.map { it[VEHICLE_ID] }

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
