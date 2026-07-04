package com.routesync.cameracount.data

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant
import java.util.concurrent.TimeUnit

/**
 * Plain PostgREST client to the shared RouteSync Supabase DB.
 * Same publishable (client-safe) key the driver app embeds.
 * The DB row is the ONLY bridge between this app and RouteSync — no app-to-app link.
 */
object SupabaseApi {

    private const val BASE = "https://vrtluruqaxutecydbrsq.supabase.co/rest/v1"
    private const val KEY = "sb_publishable_sjkjW2K7QOPRKmixJdhSgA_8rPtoFzD"
    private val JSON = "application/json".toMediaType()

    private val http = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .build()

    private fun Request.Builder.supabaseHeaders() = this
        .header("apikey", KEY)
        .header("Authorization", "Bearer $KEY")

    data class ActiveTrip(val tripId: String, val totalBoarded: Int)

    /** Poll target (every 3-5s): is there an Active trip for my bound vehicle? */
    suspend fun findActiveTrip(vehicleId: String): ActiveTrip? = withContext(Dispatchers.IO) {
        val url = "$BASE/trips?vehicle_id=eq.$vehicleId&trip_status=eq.Active" +
                "&select=trip_id,total_boarded"
        val req = Request.Builder().url(url).supabaseHeaders().get().build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("GET trips ${res.code}")
            val arr = JSONArray(res.body?.string() ?: "[]")
            if (arr.length() == 0) return@withContext null
            val row = arr.getJSONObject(0)
            ActiveTrip(row.getString("trip_id"), row.optInt("total_boarded", 0))
        }
    }

    /**
     * Count flush (every 5s while counting): one PATCH carries both the count and
     * the heartbeat — heartbeat freshness is how RouteSync knows the camera is alive.
     */
    suspend fun patchCount(tripId: String, totalBoarded: Int): Unit = withContext(Dispatchers.IO) {
        val body = JSONObject()
            .put("total_boarded", totalBoarded)
            .put("count_heartbeat", Instant.now().toString())
            .toString()
            .toRequestBody(JSON)
        val req = Request.Builder()
            .url("$BASE/trips?trip_id=eq.$tripId")
            .supabaseHeaders()
            .header("Prefer", "return=minimal")
            .patch(body)
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("PATCH trips ${res.code}")
        }
    }

    /** Phase-0 smoke check: can this device reach the DB at all? */
    suspend fun ping(): Boolean = withContext(Dispatchers.IO) {
        val req = Request.Builder()
            .url("$BASE/trips?select=trip_id&limit=1")
            .supabaseHeaders().get().build()
        http.newCall(req).execute().use { it.isSuccessful }
    }
}
