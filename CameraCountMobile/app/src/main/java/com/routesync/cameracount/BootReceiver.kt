package com.routesync.cameracount

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/**
 * Phase 9b: the counter phone is a fixed dashboard fixture. A power blip mid-route
 * reboots it; without this, the app stays closed and every trip after that goes
 * uncounted until someone climbs to the phone.
 *
 * Android 10+ blocks starting an Activity straight from a boot broadcast, so:
 *  1. try the direct launch anyway (still allowed on some OEM builds), and
 *  2. post a HIGH-importance notification with a full-screen intent — on a locked,
 *     just-booted fixture that launches the app; at worst it's a one-tap banner.
 */
class BootReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return

        // The watcher is the real recovery: it polls for trips and reopens the UI
        // itself, so even if the launches below are blocked the phone still counts.
        WatcherService.start(context)

        val launch = Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

        runCatching { context.startActivity(launch) }

        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(
                CHANNEL, "Counter auto-start",
                NotificationManager.IMPORTANCE_HIGH
            ).apply { description = "Reopens the passenger counter after the phone restarts" }
        )

        val pi = PendingIntent.getActivity(
            context, 0, launch,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notif = android.app.Notification.Builder(context, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setContentTitle("RouteSync Counter")
            .setContentText("Phone restarted. Tap to resume counting.")
            .setContentIntent(pi)
            .setFullScreenIntent(pi, true)
            .setAutoCancel(true)
            .build()

        runCatching { nm.notify(NOTIF_ID, notif) }
    }

    private companion object {
        const val CHANNEL = "boot_relaunch"
        const val NOTIF_ID = 2001
    }
}
