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
        data class Waiting(val vehicleId: String, val lastError: String?) : UiState
        data class Counting(
            val vehicleId: String,
            val tripId: String,
            val count: Int,
            val lastFlushOk: Boolean
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

    init {
        viewModelScope.launch {
            val v = prefs.vehicleId.first()
            if (v.isNullOrBlank()) _state.value = UiState.NeedsSetup
            else startPolling(v)
        }
    }

    fun bind(vehicle: String, passcode: String) {
        viewModelScope.launch {
            prefs.bind(vehicle.trim(), passcode)
            startPolling(vehicle.trim())
        }
    }

    /** Passcode-gated: release the vehicle bind and go back to Setup. */
    fun unbind(passcode: String, onResult: (Boolean) -> Unit) {
        viewModelScope.launch {
            if (!prefs.checkPasscode(passcode)) { onResult(false); return@launch }
            stopCounting()
            pollJob?.cancel()
            prefs.unbind()
            _state.value = UiState.NeedsSetup
            onResult(true)
        }
    }

    /** Phase-1 stand-in for a camera line-cross event. */
    fun increment() {
        if (tripId == null) return
        count++
        publishCounting(lastFlushOk = true)
    }

    private fun startPolling(vehicle: String) {
        vehicleId = vehicle
        _state.value = UiState.Waiting(vehicle, null)
        pollJob?.cancel()
        pollJob = viewModelScope.launch {
            while (true) {
                try {
                    val active = SupabaseApi.findActiveTrip(vehicleId)
                    when {
                        active != null && tripId == null -> {
                            // Trip started in RouteSync -> lock on, seed monotonic count.
                            tripId = active.tripId
                            count = maxOf(count, active.totalBoarded)
                            startFlushing()
                            publishCounting(lastFlushOk = true)
                        }
                        active != null && tripId == active.tripId -> {
                            // Manual may have counted while we were dead -> absorb, never lower.
                            count = maxOf(count, active.totalBoarded)
                            publishCounting(lastFlushOk = (state.value as? UiState.Counting)?.lastFlushOk ?: true)
                        }
                        active == null && tripId != null -> {
                            // Trip ended: RouteSync's finalize is the authoritative last write —
                            // do NOT flush after this, just release.
                            stopCounting()
                            _state.value = UiState.Waiting(vehicleId, null)
                        }
                    }
                    if (tripId == null && active == null) _state.value = UiState.Waiting(vehicleId, null)
                } catch (e: Exception) {
                    if (tripId == null) _state.value = UiState.Waiting(vehicleId, e.message)
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
                val ok = try {
                    SupabaseApi.patchCount(t, count); true
                } catch (_: Exception) {
                    false // offline tolerated in Phase 1; durable queue arrives in Phase 6
                }
                publishCounting(lastFlushOk = ok)
            }
        }
    }

    private fun stopCounting() {
        flushJob?.cancel()
        flushJob = null
        tripId = null
        count = 0
    }

    private fun publishCounting(lastFlushOk: Boolean) {
        val t = tripId ?: return
        _state.value = UiState.Counting(vehicleId, t, count, lastFlushOk)
    }
}
