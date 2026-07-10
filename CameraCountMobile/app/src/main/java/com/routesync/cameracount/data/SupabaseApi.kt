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
    private const val FUNCTIONS = "https://vrtluruqaxutecydbrsq.supabase.co/functions/v1"
    private const val KEY = "sb_publishable_sjkjW2K7QOPRKmixJdhSgA_8rPtoFzD"
    private val JSON = "application/json".toMediaType()

    /**
     * Phase 7: app_camera JWT from the device-token edge fn. Loaded from DataStore at
     * startup, refreshed at bind. Null -> anon key (works until the 7d cutover).
     */
    @Volatile var deviceJwt: String? = null

    private val http = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .build()

    private fun Request.Builder.supabaseHeaders() = this
        .header("apikey", KEY)
        .header("Authorization", "Bearer ${deviceJwt ?: KEY}")

    /** Outcome of a device-token mint: only Denied means the passcode was refused. */
    sealed interface TokenResult {
        data class Ok(val token: String) : TokenResult
        data object Denied : TokenResult
        data object Unreachable : TokenResult
    }

    /**
     * Phase 7 provisioning: trade the bind passcode (the fleet secret) for a 365-day
     * device JWT. The secret is only ever compared SERVER-side (edge fn env var).
     */
    suspend fun fetchDeviceToken(deviceId: String, fleetSecret: String): TokenResult =
        withContext(Dispatchers.IO) {
            val body = JSONObject()
                .put("device_id", deviceId)
                .put("fleet_secret", fleetSecret)
                .toString()
                .toRequestBody(JSON)
            val req = Request.Builder().url("$FUNCTIONS/device-token")
                .header("apikey", KEY)
                .post(body)
                .build()
            try {
                http.newCall(req).execute().use { res ->
                    when {
                        res.isSuccessful -> {
                            val token = JSONObject(res.body?.string() ?: "{}")
                                .optString("token", "")
                            if (token.isNotEmpty()) TokenResult.Ok(token) else TokenResult.Unreachable
                        }
                        res.code == 401 || res.code == 400 || res.code == 429 -> TokenResult.Denied
                        else -> TokenResult.Unreachable // fn not deployed / 5xx
                    }
                }
            } catch (_: Exception) {
                TokenResult.Unreachable
            }
        }

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

    // ------------------------------------------------------------------
    // Phase 8a — remote camera control: device_config (desired, followed)
    // + device_status (reported, echoed). See REMOTE-CONTROL-plan.md.
    // ------------------------------------------------------------------

    data class DeviceConfig(
        val ax: Float, val ay: Float, val bx: Float, val by: Float,
        val inwardSign: Int, val useBackCamera: Boolean, val version: Int
    )

    /** Follower read (piggybacks the 4s poll): the desired config for THIS device. */
    suspend fun getDeviceConfig(deviceId: String): DeviceConfig? = withContext(Dispatchers.IO) {
        val url = "$BASE/device_config?device_id=eq.$deviceId" +
                "&select=line_ax,line_ay,line_bx,line_by,inward_sign,use_back_camera,version"
        val req = Request.Builder().url(url).supabaseHeaders().get().build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("GET device_config ${res.code}")
            val arr = JSONArray(res.body?.string() ?: "[]")
            if (arr.length() == 0) return@withContext null
            val r = arr.getJSONObject(0)
            DeviceConfig(
                ax = r.optDouble("line_ax", Prefs.DEF_AX.toDouble()).toFloat(),
                ay = r.optDouble("line_ay", Prefs.DEF_AY.toDouble()).toFloat(),
                bx = r.optDouble("line_bx", Prefs.DEF_BX.toDouble()).toFloat(),
                by = r.optDouble("line_by", Prefs.DEF_BY.toDouble()).toFloat(),
                inwardSign = r.optInt("inward_sign", Prefs.DEF_INWARD_SIGN),
                useBackCamera = r.optBoolean("use_back_camera", false),
                version = r.optInt("version", 0)
            )
        }
    }

    /**
     * Local calibration writes UP (and first boot seeds the row): upsert keeps the DB
     * authoritative even though the edit happened on the phone. updated_by='device'
     * tells the other writers (driver 8b / admin 8e) who authored this version.
     */
    suspend fun upsertDeviceConfig(
        deviceId: String,
        ax: Float, ay: Float, bx: Float, by: Float,
        inwardSign: Int, useBack: Boolean, version: Int
    ): Unit = withContext(Dispatchers.IO) {
        // Android's org.json.JSONObject has NO put(String, float) overload — pass Double
        // or it throws NoSuchMethodError at runtime.
        val body = JSONObject()
            .put("device_id", deviceId)
            .put("line_ax", ax.toDouble()).put("line_ay", ay.toDouble())
            .put("line_bx", bx.toDouble()).put("line_by", by.toDouble())
            .put("inward_sign", inwardSign)
            .put("use_back_camera", useBack)
            .put("version", version)
            .put("updated_by", "device")
            .put("updated_at", Instant.now().toString())
            .toString().toRequestBody(JSON)
        val req = Request.Builder().url("$BASE/device_config").supabaseHeaders()
            .header("Prefer", "resolution=merge-duplicates,return=minimal")
            .post(body)
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("UPSERT device_config ${res.code}")
        }
    }

    /**
     * Reported state: liveness heartbeat + which config version this device runs.
     * Driver/web show ✓ only when config_version_applied == device_config.version.
     * [justApplied] stamps applied_at (a fresh apply, not just a heartbeat).
     */
    suspend fun upsertDeviceStatus(
        deviceId: String, configVersionApplied: Int, justApplied: Boolean = false
    ): Unit = withContext(Dispatchers.IO) {
        val body = JSONObject()
            .put("device_id", deviceId)
            .put("last_seen", Instant.now().toString())
            .put("config_version_applied", configVersionApplied)
            .apply { if (justApplied) put("applied_at", Instant.now().toString()) }
            .toString().toRequestBody(JSON)
        val req = Request.Builder().url("$BASE/device_status").supabaseHeaders()
            .header("Prefer", "resolution=merge-duplicates,return=minimal")
            .post(body)
            .build()
        http.newCall(req).execute().use { res ->
            if (!res.isSuccessful) throw IllegalStateException("UPSERT device_status ${res.code}")
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
