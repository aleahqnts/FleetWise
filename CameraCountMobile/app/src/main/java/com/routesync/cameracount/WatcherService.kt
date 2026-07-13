package com.routesync.cameracount

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.provider.Settings
import com.routesync.cameracount.data.Prefs
import com.routesync.cameracount.data.SupabaseApi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

/**
 * Phase 9b: always-on trip watcher. The driver must NEVER have to touch the camera
 * phone: when Start Trip is tapped on the driver app, THIS service (alive even with the
 * UI closed) sees the Active trip on the next poll and brings MainActivity up itself.
 *
 * Launch path: direct startActivity when "Display over other apps" is granted
 * (one-time install setup); otherwise a full-screen-intent notification, which
 * auto-launches on a locked/screen-off fixture and is a one-tap banner otherwise.
 *
 * Runs from boot (BootReceiver) and from every app open. Skips work while the UI is
 * visible — the ViewModel's own 4s poll owns the live path then.
 */
class WatcherService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var loop: Job? = null

    override fun onBind(intent: Intent?) = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForeground(NOTIF_ID, buildNotification())
        if (loop == null) loop = scope.launch { watch() }
        return START_STICKY
    }

    private suspend fun watch() {
        val prefs = Prefs(this)
        while (true) {
            try {
                // UI on screen -> its ViewModel handles trips; nothing to do here.
                if (!MainActivity.uiVisible) {
                    val vehicle = prefs.vehicleId.first()
                    if (!vehicle.isNullOrBlank()) {
                        if (SupabaseApi.deviceJwt == null) SupabaseApi.deviceJwt = prefs.deviceJwt()
                        val active = SupabaseApi.findActiveTrip(vehicle)
                        if (active != null) launchUi()
                    }
                }
            } catch (_: Exception) {
                // Offline / transient: try again next tick.
            }
            delay(15_000)
        }
    }

    private fun launchUi() {
        val launch = Intent(this, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

        // Direct background launch is legal with the overlay grant; otherwise fall
        // back to a full-screen-intent notification.
        if (Settings.canDrawOverlays(this)) {
            if (runCatching { startActivity(launch) }.isSuccess) return
        }

        val pi = PendingIntent.getActivity(
            this, 1, launch,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val notif = Notification.Builder(this, ensureChannel(CHANNEL_TRIP, "Trip started"))
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setContentTitle("Trip started")
            .setContentText("Tap to start counting passengers.")
            .setContentIntent(pi)
            .setFullScreenIntent(pi, true)
            .setAutoCancel(true)
            .build()
        runCatching {
            (getSystemService(NOTIFICATION_SERVICE) as NotificationManager).notify(TRIP_NOTIF_ID, notif)
        }
    }

    private fun buildNotification(): Notification {
        val pi = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        return Notification.Builder(this, ensureChannel(CHANNEL_STANDBY, "Counter standby"))
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setContentTitle("RouteSync Counter")
            .setContentText("Standby: watching for trips.")
            .setContentIntent(pi)
            .setOngoing(true)
            .build()
    }

    private fun ensureChannel(id: String, name: String): String {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        val importance = if (id == CHANNEL_TRIP) NotificationManager.IMPORTANCE_HIGH
        else NotificationManager.IMPORTANCE_MIN
        nm.createNotificationChannel(NotificationChannel(id, name, importance))
        return id
    }

    override fun onDestroy() {
        loop?.cancel()
        super.onDestroy()
    }

    companion object {
        private const val CHANNEL_STANDBY = "watcher_standby"
        private const val CHANNEL_TRIP = "watcher_trip"
        private const val NOTIF_ID = 3001
        private const val TRIP_NOTIF_ID = 3002

        /** Idempotent: safe to call from boot, app open, or bind. */
        fun start(context: Context) {
            val intent = Intent(context, WatcherService::class.java)
            runCatching {
                if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(intent)
                else context.startService(intent)
            }
        }
    }
}
