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
     * Trip claim (double-link safeguard): atomically stamp our device ID onto the trip,
     * but ONLY if it's unclaimed, already ours, or the current owner's heartbeat went
     * silent >30s (owner died/overheated -> standby phone takes over). The WHERE filter
     * makes the claim race-safe: two phones claiming at once -> exactly one row match
     * wins. Claim also seeds the heartbeat so a freshly claimed trip is never "stale".
     */
    suspend fun claimTrip(tripId: String, deviceId: String): Boolean = withContext(Dispatchers.IO) {
        val staleCut = Instant.now().minusSeconds(30).toString()
        val url = "$BASE/trips?trip_id=eq.$tripId" +
                "&or=(counter_device_id.is.null,counter_device_id.eq.$deviceId,count_heartbeat.lt.$staleCut)"
        val body = JSONObject()
            .put("counter_device_id", deviceId)
            .put("count_heartbeat", Instant.now().toString())
            .toString()
            .toRequestBody(JSON)
        val req = Request.Builder().url(url).supabaseHeaders()
            .header("Prefer", "return=representation")
            .patch(body)
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("PATCH claim ${res.code}")
            JSONArray(res.body?.string() ?: "[]").length() > 0 // 0 rows = someone else owns it
        }
    }

    /**
     * Count flush (every 5s while counting): one PATCH carries both the count and
     * the heartbeat — heartbeat freshness is how RouteSync knows the camera is alive.
     * Owner-guarded: if another device stole the claim, this matches 0 rows and returns
     * false so the caller demotes itself instead of silently fighting over the row.
     */
    suspend fun patchCount(tripId: String, totalBoarded: Int, deviceId: String): Boolean =
        withContext(Dispatchers.IO) {
            val body = JSONObject()
                .put("total_boarded", totalBoarded)
                .put("count_heartbeat", Instant.now().toString())
                .toString()
                .toRequestBody(JSON)
            val req = Request.Builder()
                .url("$BASE/trips?trip_id=eq.$tripId&counter_device_id=eq.$deviceId")
                .supabaseHeaders()
                .header("Prefer", "return=representation")
                .patch(body)
                .build()
            http.newCall(req).execute().use { res ->
                if (!res.isSuccessful) throw IllegalStateException("PATCH trips ${res.code}")
                JSONArray(res.body?.string() ?: "[]").length() > 0
            }
        }

    /**
     * Bind-level lock (one counter phone per bus, EVER): binding claims the vehicle row
     * itself. A second phone's bind is refused outright — deployment never has two phones
     * on one bus, so a second bind is always a mistake. Atomic like the trip claim.
     */
    suspend fun claimVehicle(vehicleId: String, deviceId: String): Boolean = withContext(Dispatchers.IO) {
        val url = "$BASE/vehicles?vehicle_id=eq.$vehicleId" +
                "&or=(counter_device_id.is.null,counter_device_id.eq.$deviceId)"
        val body = JSONObject().put("counter_device_id", deviceId).toString().toRequestBody(JSON)
        val req = Request.Builder().url(url).supabaseHeaders()
            .header("Prefer", "return=representation")
            .patch(body)
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("PATCH vehicles ${res.code}")
            JSONArray(res.body?.string() ?: "[]").length() > 0
        }
    }

    /** Unbind releases the vehicle lock so a replacement phone can bind. */
    suspend fun releaseVehicle(vehicleId: String, deviceId: String): Unit = withContext(Dispatchers.IO) {
        val url = "$BASE/vehicles?vehicle_id=eq.$vehicleId&counter_device_id=eq.$deviceId"
        val body = JSONObject().put("counter_device_id", JSONObject.NULL).toString().toRequestBody(JSON)
        val req = Request.Builder().url(url).supabaseHeaders()
            .header("Prefer", "return=minimal")
            .patch(body)
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("PATCH vehicles ${res.code}")
        }
    }

    /**
     * Post-trip reconcile: the ONE write allowed after a trip ends. Scenario: camera in a
     * dead zone counts 16, driver's manual fallback wrote 9, trip ends before the camera
     * reconnects -> DB records 9 and the 16 would die on the phone. This PATCH raises
     * total_boarded IFF ours is higher (total_boarded=lt.N filter -> atomic, raise-only,
     * can never clobber finalize) and only while we're still the claimed counter device.
     * No heartbeat in the body: the trip is over, nothing should look alive.
     */
    suspend fun reconcileFinalCount(tripId: String, deviceId: String, totalBoarded: Int): Unit =
        withContext(Dispatchers.IO) {
            val url = "$BASE/trips?trip_id=eq.$tripId&counter_device_id=eq.$deviceId" +
                    "&total_boarded=lt.$totalBoarded"
            val body = JSONObject().put("total_boarded", totalBoarded).toString().toRequestBody(JSON)
            val req = Request.Builder().url(url).supabaseHeaders()
                .header("Prefer", "return=minimal")
                .patch(body)
                .build()
            http.newCall(req).execute().use { res ->
                if (!res.isSuccessful) throw IllegalStateException("PATCH reconcile ${res.code}")
            }
        }

    data class FleetVehicle(val vehicleId: String, val plate: String)

    /** Setup dropdown: the whole fleet, so installers pick instead of typing. */
    suspend fun listVehicles(): List<FleetVehicle> = withContext(Dispatchers.IO) {
        val url = "$BASE/vehicles?select=vehicle_id,plate_number&order=vehicle_id"
        val req = Request.Builder().url(url).supabaseHeaders().get().build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("GET vehicles ${res.code}")
            val arr = JSONArray(res.body?.string() ?: "[]")
            (0 until arr.length()).map {
                val row = arr.getJSONObject(it)
                FleetVehicle(row.getString("vehicle_id"), row.optString("plate_number", ""))
            }
        }
    }

    /**
     * Bind-time validation: does this vehicle actually exist? Returns its plate number
     * (shown to the installer as confirmation they bound the right bus) or null.
     */
    suspend fun findVehiclePlate(vehicleId: String): String? = withContext(Dispatchers.IO) {
        val url = "$BASE/vehicles?vehicle_id=eq.$vehicleId&select=vehicle_id,plate_number"
        val req = Request.Builder().url(url).supabaseHeaders().get().build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("GET vehicles ${res.code}")
            val arr = JSONArray(res.body?.string() ?: "[]")
            if (arr.length() == 0) return@withContext null
            arr.getJSONObject(0).optString("plate_number", "")
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
