package com.routesync.cameracount

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat

/**
 * Phase 6: foreground service held for the duration of an Active trip.
 *
 * The activity does the actual work (camera + tracker + flush loops live in the
 * ViewModel); this service exists so Android treats the process as foreground —
 * a screen-dim, notification-shade pull, or brief app switch on the dashboard phone
 * must not kill the counter mid-trip. Started when the trip locks on, stopped when
 * the trip ends.
 */
class CountingService : Service() {

    companion object {
        private const val CHANNEL_ID = "counting"
        private const val NOTIF_ID = 1

        fun start(context: Context, vehicleId: String) {
            val i = Intent(context, CountingService::class.java)
                .putExtra("vehicle", vehicleId)
            ContextCompat.startForegroundService(context, i)
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, CountingService::class.java))
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val vehicle = intent?.getStringExtra("vehicle") ?: ""
        val notif = buildNotification(vehicle)
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIF_ID, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            startForeground(NOTIF_ID, notif)
        }
        // Trip lifecycle is owned by the ViewModel's poll loop, not the OS — if the
        // system kills us anyway, don't auto-restart into a stale trip.
        return START_NOT_STICKY
    }

    private fun buildNotification(vehicle: String): Notification {
        if (Build.VERSION.SDK_INT >= 26) {
            val ch = NotificationChannel(
                CHANNEL_ID, "Passenger counting",
                NotificationManager.IMPORTANCE_LOW // silent, no sound on the dashboard
            )
            getSystemService(NotificationManager::class.java).createNotificationChannel(ch)
        }
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setContentTitle("RouteSync Counter")
            .setContentText("Counting passengers${if (vehicle.isNotBlank()) " · $vehicle" else ""}")
            .setOngoing(true)
            .build()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
