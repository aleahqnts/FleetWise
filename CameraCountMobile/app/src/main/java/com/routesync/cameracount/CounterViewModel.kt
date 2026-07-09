package com.routesync.cameracount

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.routesync.cameracount.data.Prefs
import com.routesync.cameracount.data.SupabaseApi
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

/**
 * Phase-1 core: the whole RouteSync bridge, with a fake +1 button standing in for the
 * camera. Phase 4 swaps the button for YOLO line-cross events — everything else stays.
 *
 * Poll (4s):  GET Active trip for the bound vehicle -> lock on / release.
 * Flush (5s): one PATCH carries total_boarded + count_heartbeat (heartbeat freshness is
 *             how RouteSync decides to hide/show its manual counter).
 * Monotonic:  seed local count from DB on acquire; only ever raise it (max(db, local)) ->
 *             restarts, manual hand-offs, and reconnects reconcile without double counts.
 */
class CounterViewModel(app: Application) : AndroidViewModel(app) {

    sealed interface UiState {
        data object NeedsSetup : UiState
        data class Waiting(
            val vehicleId: String,
            val lastError: String?,
            val plate: String? = null,
            val tripSummary: String? = null // "43 boarded" — shown after a trip ends
        ) : UiState
        /** Double-link safeguard: another camera phone owns this trip; we watch, don't count. */
        data class Standby(val vehicleId: String, val tripId: String) : UiState
        data class Counting(
            val vehicleId: String,
            val tripId: String,
            val count: Int,
            val lastFlushOk: Boolean,
            val cameraStalled: Boolean = false
        ) : UiState
    }

    private val prefs = Prefs(app)
    private val _state = MutableStateFlow<UiState>(UiState.NeedsSetup)
    val state: StateFlow<UiState> = _state

    private var pollJob: Job? = null
    private var flushJob: Job? = null
    private var tripId: String? = null
    private var count = 0
    private var vehicleId: String = ""
    private var plate: String? = null
    private var deviceId: String = ""
    private var lastSummary: String? = null

    private fun waiting(err: String? = null) =
        UiState.Waiting(vehicleId, err, plate, lastSummary)

    /** Phase 6 restart resume: (tripId, count) saved before we died; consumed on re-acquire. */
    private var restored: Prefs.PendingCount? = null

    /**
     * Phase 6 stall guard: the camera pipeline pings this every analyzed frame. If frames
     * stop (camera stall, thermal shutdown of the pipeline) the flush loop STOPS writing
     * heartbeats — going silent is the point: a stale heartbeat is exactly what makes the
     * driver's manual counter reappear. A fake-alive heartbeat would hide a dead camera.
     */
    @Volatile private var lastFrameAt = 0L
    private val stallAfterMs = 12_000L

    fun noteFrame() { lastFrameAt = android.os.SystemClock.elapsedRealtime() }
    private fun cameraStalled() =
        android.os.SystemClock.elapsedRealtime() - lastFrameAt > stallAfterMs

    init {
        viewModelScope.launch {
            deviceId = prefs.deviceId()
            // Phase 7: attach the stored device JWT before the first DB call.
            SupabaseApi.deviceJwt = prefs.deviceJwt()
            restored = prefs.pendingCount() // survives kill/reboot mid-trip
            plate = prefs.plate.first()
            val v = prefs.vehicleId.first()
            if (v.isNullOrBlank()) _state.value = UiState.NeedsSetup
            else startPolling(v)
        }
    }

    companion object {
        /** Fleet convention: V + 3 digits (V001..V012 today, room to grow). */
        val VEHICLE_ID_RE = Regex("^V\\d{3}$")
        const val MIN_PASSCODE = 4
    }

    /**
     * Validated bind: format is checked in the UI; here we verify the vehicle actually
     * EXISTS in the fleet before committing — a typo'd bind would otherwise sit silently
     * "waiting for trip" forever. onResult(null) = bound; else a user-facing error.
     */
    fun bind(vehicle: String, passcode: String, onResult: (String?) -> Unit) {
        val v = vehicle.trim().uppercase()
        viewModelScope.launch {
            // Phase 7 (7d: JWT-only): mint the device JWT FIRST — every call below
            // requires it (anon has zero DB access). Wrong passcode or no server ->
            // bind refused outright.
            when (val tok = SupabaseApi.fetchDeviceToken(deviceId, passcode)) {
                is SupabaseApi.TokenResult.Ok -> {
                    prefs.saveDeviceJwt(tok.token)
                    SupabaseApi.deviceJwt = tok.token
                }
                SupabaseApi.TokenResult.Denied -> {
                    onResult("Wrong fleet passcode — binding is verified by the server now.")
                    return@launch
                }
                SupabaseApi.TokenResult.Unreachable -> {
                    onResult("Can't reach the server — check the internet connection and try again.")
                    return@launch
                }
            }
            val p = try {
                SupabaseApi.findVehiclePlate(v)
            } catch (_: Exception) {
                onResult("Can't reach the server — check the internet connection and try again.")
                return@launch
            }
            if (p == null) {
                onResult("Vehicle $v is not in the fleet. Double-check the ID on the dashboard sticker.")
                return@launch
            }
            // One counter phone per bus: claim the vehicle row or refuse the bind.
            val claimed = try {
                SupabaseApi.claimVehicle(v, deviceId)
            } catch (_: Exception) {
                onResult("Can't reach the server — check the internet connection and try again.")
                return@launch
            }
            if (!claimed) {
                onResult(
                    "$v already has a counter phone bound to it. Unbind that phone first " +
                        "(or ask the admin to clear the lock)."
                )
                return@launch
            }
            prefs.bind(v, passcode, p)
            plate = p
            lastSummary = null
            startPolling(v)
            onResult(null)
        }
    }

    /** Passcode-gated: release the vehicle bind (and its DB lock) and go back to Setup. */
    fun unbind(passcode: String, onResult: (Boolean) -> Unit) {
        viewModelScope.launch {
            if (!prefs.checkPasscode(passcode)) { onResult(false); return@launch }
            stopCounting()
            pollJob?.cancel()
            // Best-effort lock release: offline unbind still unbinds locally — the DB lock
            // then needs the admin to clear vehicles.counter_device_id manually.
            runCatching { SupabaseApi.releaseVehicle(vehicleId, deviceId) }
            prefs.unbind()
            _state.value = UiState.NeedsSetup
            onResult(true)
        }
    }

    /** Called by the camera pipeline on each inward line-cross. */
    fun increment() {
        val t = tripId ?: return
        count++
        persistPending(t)
        publishCounting(lastFlushOk = true)
    }

    /** Durable write-behind: tiny DataStore commit per change, cheap at boarding rates. */
    private fun persistPending(t: String) {
        val c = count
        viewModelScope.launch { prefs.savePendingCount(t, c) }
    }

    private fun startPolling(vehicle: String) {
        vehicleId = vehicle
        _state.value = waiting()
        pollJob?.cancel()
        pollJob = viewModelScope.launch {
            while (true) {
                try {
                    val active = SupabaseApi.findActiveTrip(vehicleId)
                    when {
                        active != null && tripId == null -> {
                            // Claim first (double-link safeguard): only ONE camera phone may
                            // count a trip. Losing the claim -> Standby; we retry every poll,
                            // and the claim's stale-heartbeat rule lets us take over if the
                            // owner dies (>30s silent).
                            if (!SupabaseApi.claimTrip(active.tripId, deviceId)) {
                                _state.value = UiState.Standby(vehicleId, active.tripId)
                            } else {
                                // Lock on, seed monotonic count. If we died mid-trip and THIS
                                // is still that trip, resume from the persisted local count too
                                // (dead-zone counts survive restart, flush on reconnect).
                                tripId = active.tripId
                                val saved = restored?.takeIf { it.tripId == active.tripId }?.count ?: 0
                                // Pending from an OLDER trip (ended while we were dead, new trip
                                // already started) still deserves its reconcile before we move on.
                                restored?.takeIf { it.tripId != active.tripId }
                                    ?.let { reconcileAndClear(it.tripId, it.count) }
                                restored = null
                                count = maxOf(count, active.totalBoarded, saved)
                                persistPending(active.tripId)
                                lastFrameAt = android.os.SystemClock.elapsedRealtime() // camera warm-up grace
                                CountingService.start(getApplication(), vehicleId)
                                startFlushing()
                                publishCounting(lastFlushOk = true)
                            }
                        }
                        active != null && tripId == active.tripId -> {
                            // Manual may have counted while we were dead -> absorb, never lower.
                            count = maxOf(count, active.totalBoarded)
                            publishCounting(lastFlushOk = (state.value as? UiState.Counting)?.lastFlushOk ?: true)
                        }
                        active == null && tripId != null -> {
                            // Trip ended. Finalize owns status/times, but total_boarded gets
                            // ONE raise-only reconcile: if we counted in a dead zone while the
                            // driver's manual fallback wrote less, our higher count must land
                            // even though the trip already closed. Raise-only filter -> the
                            // normal online case is a harmless 0-row no-op.
                            val endedTrip = tripId!!
                            val finalCount = count
                            lastSummary = "$finalCount boarded"
                            stopCounting()
                            reconcileAndClear(endedTrip, finalCount)
                            _state.value = waiting()
                        }
                    }
                    if (tripId == null && active == null) {
                        // App (re)started after the trip already ended, counts still on disk
                        // (phone died offline mid-trip, rebooted later) -> reconcile them now.
                        restored?.let { r ->
                            restored = null
                            if (r.count > 0) lastSummary = "${r.count} boarded (recovered)"
                            reconcileAndClear(r.tripId, r.count)
                        }
                        _state.value = waiting()
                    }
                } catch (e: Exception) {
                    if (tripId == null) _state.value = waiting(e.message)
                    // While counting, poll errors are tolerated; flush loop keeps trying.
                }
                delay(4_000)
            }
        }
    }

    private fun startFlushing() {
        flushJob?.cancel()
        flushJob = viewModelScope.launch {
            while (true) {
                delay(5_000)
                val t = tripId ?: break
                if (cameraStalled()) {
                    // Camera dead -> stop heartbeating so RouteSync reveals manual within
                    // 12s. The local count is kept + persisted; if frames come back we
                    // resume flushing and max(db, local) reconciles any manual taps.
                    publishCounting(lastFlushOk = false, cameraStalled = true)
                    continue
                }
                val stillOwner = try {
                    SupabaseApi.patchCount(t, count, deviceId)
                } catch (_: Exception) {
                    publishCounting(lastFlushOk = false) // offline: count persisted locally
                    continue
                }
                if (!stillOwner) {
                    // Another device took the claim (we looked dead >30s, it stole per the
                    // rule). Discard local count — the new owner seeds from DB and counts
                    // from here; keeping ours would inflate on a later re-claim.
                    count = 0
                    val trip = t
                    stopCounting()
                    _state.value = UiState.Standby(vehicleId, trip)
                    break
                }
                publishCounting(lastFlushOk = true)
            }
        }
    }

    private fun stopCounting() {
        flushJob?.cancel()
        flushJob = null
        tripId = null
        count = 0
        CountingService.stop(getApplication())
        // Pending count is NOT cleared here: it must survive until the post-trip
        // reconcile lands (reconcileAndClear), or a dead-zone count would be lost.
    }

    /**
     * Push our final count if it beats the DB (raise-only), then drop the persisted
     * pending. Reconcile failing (still offline / blip) keeps the pending on disk via
     * [restored], so the next poll pass — or the next app start — retries it.
     */
    private fun reconcileAndClear(trip: String, finalCount: Int) {
        viewModelScope.launch {
            try {
                if (finalCount > 0) SupabaseApi.reconcileFinalCount(trip, deviceId, finalCount)
                // Clear only OUR pending — a new trip may have written fresh pending since.
                if (prefs.pendingCount()?.tripId == trip) prefs.clearPendingCount()
            } catch (_: Exception) {
                restored = Prefs.PendingCount(trip, finalCount) // retry on a later pass
            }
        }
    }

    private fun publishCounting(lastFlushOk: Boolean, cameraStalled: Boolean = false) {
        val t = tripId ?: return
        _state.value = UiState.Counting(vehicleId, t, count, lastFlushOk, cameraStalled)
    }
}
