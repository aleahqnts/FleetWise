package com.routesync.cameracount

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewmodel.compose.viewModel
import kotlinx.coroutines.flow.first
import com.routesync.cameracount.ui.*

/**
 * CameraCount Mobile — RouteSync's camera-based passenger counter.
 * Phase 1: vehicle bind + trip poll + fake +1 counter proving the DB bridge.
 * UI mirrors the RouteSync driver-app / dashboard theme (see ui/Theme.kt).
 */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Phase 9b: the watcher outlives the UI — it reopens this activity when a trip
        // starts while the app is closed/backgrounded. Started here so it's always
        // alive once the app has been opened once after install.
        WatcherService.start(this)
        setContent { RsTheme { Root() } }
    }

    override fun onResume() { super.onResume(); uiVisible = true }
    override fun onPause() { super.onPause(); uiVisible = false }

    companion object {
        /** Watcher skips trip-launch polling while the UI is on screen. */
        @Volatile var uiVisible = false
    }
}

/** One overlay-settings hop per app start (re-asked on next start if still denied). */
private var overlayPrompted = false

private fun promptOverlayPermission(context: android.content.Context) {
    if (overlayPrompted || android.provider.Settings.canDrawOverlays(context)) return
    overlayPrompted = true
    runCatching {
        context.startActivity(
            android.content.Intent(
                android.provider.Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                android.net.Uri.parse("package:${context.packageName}")
            ).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }
}

@Composable
fun Root(vm: CounterViewModel = viewModel()) {
    var showPreview by remember { mutableStateOf(false) }
    val s = vm.state.collectAsState().value

    // Phase 6: the trip foreground-service notification needs this on API 33+.
    // Phase 9b: "Display over other apps" is REQUIRED (the watcher opens this app when
    // a trip starts). Android has no dialog for it, only its Settings page — so open
    // that page automatically, chained AFTER the notification dialog so the two system
    // prompts never stack. Once per app start until granted.
    val context = androidx.compose.ui.platform.LocalContext.current
    val askNotif = androidx.activity.compose.rememberLauncherForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.RequestPermission()
    ) { promptOverlayPermission(context) }
    LaunchedEffect(Unit) {
        if (android.os.Build.VERSION.SDK_INT >= 33 &&
            androidx.core.content.ContextCompat.checkSelfPermission(
                context, android.Manifest.permission.POST_NOTIFICATIONS
            ) != android.content.pm.PackageManager.PERMISSION_GRANTED
        ) askNotif.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        else promptOverlayPermission(context)
    }

    // Trip active -> this device IS the counter. Camera + tracker run for the whole
    // trip and stop when the trip ends (state leaves Counting -> screen disposed).
    if (s is CounterViewModel.UiState.Counting) {
        CameraScreen(vm = vm)
        return
    }
    // Calibration (preview + draggable line), reachable while waiting.
    if (showPreview) {
        CameraScreen(calibrate = true, onClose = { showPreview = false })
        return
    }
    RsBackground {
        Column(
            Modifier.fillMaxSize().systemBarsPadding().padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            when (s) {
                is CounterViewModel.UiState.NeedsSetup -> SetupCard(vm, onBind = vm::bind)
                is CounterViewModel.UiState.Waiting -> WaitingCard(vm, s, onCamera = { showPreview = true })
                is CounterViewModel.UiState.Standby -> StandbyCard(vm, s)
                else -> {}
            }
        }
    }
}

@Composable
@OptIn(ExperimentalMaterial3Api::class)
private fun SetupCard(vm: CounterViewModel, onBind: (String, String, (String?) -> Unit) -> Unit) {
    var vehicle by remember { mutableStateOf("") }
    var passcode by remember { mutableStateOf("") }
    var touchedVehicle by remember { mutableStateOf(false) }
    var binding by remember { mutableStateOf(false) }
    var bindError by remember { mutableStateOf<String?>(null) }

    // Post-7d: the fleet list is behind auth, so the passcode comes FIRST. A verified
    // passcode mints the device token, then the bus dropdown loads with it.
    var fleet by remember { mutableStateOf<List<com.routesync.cameracount.data.SupabaseApi.FleetVehicle>?>(null) }
    var fleetError by remember { mutableStateOf<String?>(null) }
    var checking by remember { mutableStateOf(false) }
    var serverDown by remember { mutableStateOf(false) }

    val passOk = passcode.length >= CounterViewModel.MIN_PASSCODE

    // Debounced: retyping cancels the previous attempt (LaunchedEffect restart).
    LaunchedEffect(passcode) {
        fleet = null; fleetError = null; serverDown = false; vehicle = ""
        if (!passOk) return@LaunchedEffect
        kotlinx.coroutines.delay(900)
        checking = true
        vm.prepareFleet(passcode) { list, err ->
            checking = false
            fleet = list
            fleetError = err
            serverDown = err?.contains("server") == true
        }
    }

    val vehicleOk = CounterViewModel.VEHICLE_ID_RE.matches(vehicle)

    RsWordmark("Passenger Counter")
    Spacer(Modifier.height(24.dp))
    RsCard {
        Text("Set up this device", fontSize = 20.sp, fontWeight = FontWeight.Bold, color = RsColor.Navy)
        Spacer(Modifier.height(4.dp))
        Text("Bind this phone to the bus it is mounted in.", color = RsColor.Muted)
        Spacer(Modifier.height(20.dp))

        OutlinedTextField(
            passcode, { passcode = it; bindError = null }, singleLine = true,
            label = { Text("Fleet passcode") }, modifier = Modifier.fillMaxWidth(),
            visualTransformation = PasswordVisualTransformation(),
            supportingText = {
                if (!passOk) Text("Enter the fleet passcode to load the bus list.", color = RsColor.Muted)
            }
        )
        Spacer(Modifier.height(12.dp))

        when {
            checking -> Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(10.dp))
                Text("Checking passcode…", color = RsColor.Muted)
            }
            fleet != null -> {
                // Picker: no typos possible; shows the plate so the installer matches the bus.
                var open by remember { mutableStateOf(false) }
                ExposedDropdownMenuBox(expanded = open, onExpandedChange = { open = it }) {
                    OutlinedTextField(
                        value = fleet!!.firstOrNull { it.vehicleId == vehicle }
                            ?.let { "${it.vehicleId} · ${it.plate}" } ?: "",
                        onValueChange = {}, readOnly = true,
                        label = { Text("Select vehicle") },
                        trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(open) },
                        modifier = Modifier.fillMaxWidth().menuAnchor()
                    )
                    ExposedDropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                        fleet!!.forEach { v ->
                            DropdownMenuItem(
                                text = { Text("${v.vehicleId} · ${v.plate}") },
                                onClick = { vehicle = v.vehicleId; bindError = null; open = false }
                            )
                        }
                    }
                }
            }
            serverDown -> {
                // Offline: manual entry with format validation. The bind itself will
                // retry the network anyway.
                OutlinedTextField(
                    vehicle,
                    {
                        // Fleet IDs are V + digits: uppercase, strip anything else, cap at 4 chars.
                        vehicle = it.uppercase().filter { c -> c == 'V' || c.isDigit() }.take(4)
                        touchedVehicle = true; bindError = null
                    },
                    singleLine = true,
                    label = { Text("Vehicle ID (e.g. V001)") }, modifier = Modifier.fillMaxWidth(),
                    isError = touchedVehicle && vehicle.isNotEmpty() && !vehicleOk,
                    supportingText = {
                        if (touchedVehicle && vehicle.isNotEmpty() && !vehicleOk)
                            Text("Format: V + 3 digits, e.g. V001", color = RsColor.Error)
                        else Text("Offline: type the vehicle ID from the dashboard sticker.", color = RsColor.Muted)
                    }
                )
            }
        }
        fleetError?.takeIf { !serverDown }?.let {
            Spacer(Modifier.height(8.dp))
            Text(it, color = RsColor.Error)
        }
        bindError?.let {
            Spacer(Modifier.height(8.dp))
            Text(it, color = RsColor.Error)
        }
        Spacer(Modifier.height(20.dp))
        PrimaryButton(
            if (binding) "Checking vehicle…" else "Bind vehicle",
            enabled = vehicleOk && passOk && !binding
        ) {
            binding = true
            onBind(vehicle, passcode) { err ->
                binding = false
                bindError = err // null = bound; Root switches to Waiting via state
            }
        }
    }
}

@Composable
private fun WaitingCard(vm: CounterViewModel, s: CounterViewModel.UiState.Waiting, onCamera: () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val prefs = remember { com.routesync.cameracount.data.Prefs(context) }

    // Deploy trap: a bound phone whose line was never calibrated counts garbage against
    // the default mid-screen line. Nag until the installer calibrates once.
    var lineIsDefault by remember { mutableStateOf(false) }
    var deviceId by remember { mutableStateOf("") }
    LaunchedEffect(Unit) {
        val cal = prefs.lineCalibration.first()
        lineIsDefault = cal.ax == com.routesync.cameracount.data.Prefs.DEF_AX &&
            cal.ay == com.routesync.cameracount.data.Prefs.DEF_AY &&
            cal.bx == com.routesync.cameracount.data.Prefs.DEF_BX &&
            cal.by == com.routesync.cameracount.data.Prefs.DEF_BY
        deviceId = prefs.deviceId()
    }

    Header(vm, s.vehicleId, onCamera)
    Spacer(Modifier.height(20.dp))
    if (lineIsDefault) {
        Row(
            Modifier.widthIn(max = 380.dp).fillMaxWidth()
                .clip(RoundedCornerShape(12.dp))
                .background(RsColor.Mint1)
                .padding(horizontal = 14.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(Modifier.weight(1f)) {
                Text("Counting line not calibrated", fontWeight = FontWeight.Bold, color = RsColor.Navy, fontSize = 14.sp)
                Text("Counts may be wrong until the line is placed on the doorway.", color = RsColor.Muted, fontSize = 12.sp)
            }
            TextButton(onClick = onCamera) { Text("Calibrate", color = RsColor.Teal, fontWeight = FontWeight.Bold) }
        }
        Spacer(Modifier.height(12.dp))
    }
    RsCard {
        Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
            StatusDot(active = false)
            Spacer(Modifier.height(12.dp))
            Text("Waiting for trip", fontSize = 20.sp, fontWeight = FontWeight.Bold, color = RsColor.Navy)
            // Bound-bus confirmation: ID + plate, so a wrong bind is obvious at a glance.
            s.plate?.takeIf { it.isNotBlank() }?.let {
                Spacer(Modifier.height(4.dp))
                Text("${s.vehicleId} · $it", color = RsColor.Teal, fontWeight = FontWeight.Bold)
            }
            Spacer(Modifier.height(8.dp))
            Text(
                "Counting starts automatically when the driver starts a trip for ${s.vehicleId}.",
                color = RsColor.Muted, textAlign = TextAlign.Center
            )
            // Last run's result sticks around until the next trip starts.
            s.tripSummary?.let {
                Spacer(Modifier.height(12.dp))
                Text(
                    "Last trip · $it", color = RsColor.Navy, fontWeight = FontWeight.Bold,
                    modifier = Modifier.clip(RoundedCornerShape(8.dp))
                        .background(RsColor.Mint2).padding(horizontal = 12.dp, vertical = 6.dp)
                )
            }
            s.lastError?.let {
                Spacer(Modifier.height(12.dp))
                Text(
                    "Offline, retrying…", color = RsColor.Error, fontWeight = FontWeight.Bold,
                    modifier = Modifier.clip(RoundedCornerShape(8.dp))
                        .background(RsColor.Mint1).padding(horizontal = 12.dp, vertical = 6.dp)
                )
            }
        }
    }
    // Ops footer: device id matches trips/vehicles.counter_device_id in the DB — needed
    // when an admin has to identify or clear this phone's lock.
    if (deviceId.isNotBlank()) {
        Spacer(Modifier.height(14.dp))
        Text("RouteSync Counter · $deviceId", color = RsColor.Muted, fontSize = 11.sp)
    }
}

/**
 * FAULT state — deployment is strictly one counter phone per bus, and the bind-level
 * vehicle lock should make this unreachable. Seeing it means two devices ended up bound
 * to the same bus anyway (offline bind race, manually cleared lock): the trip claim
 * stopped the double count, and this screen tells the operator to fix the root cause.
 * (If the counting device dies >30s, this one recovers the trip so counts keep flowing.)
 */
@Composable
private fun StandbyCard(vm: CounterViewModel, s: CounterViewModel.UiState.Standby) {
    Header(vm, s.vehicleId, onCamera = {})
    Spacer(Modifier.height(20.dp))
    RsCard {
        Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
            StatusDot(active = false)
            Spacer(Modifier.height(12.dp))
            Text("⚠ Two counter phones detected", fontSize = 20.sp, fontWeight = FontWeight.Bold, color = RsColor.Error)
            Spacer(Modifier.height(8.dp))
            Text(
                "Another device is already counting trip ${s.tripId} on ${s.vehicleId}. " +
                    "Each bus must have exactly ONE counter phone. Unbind the phone that " +
                    "doesn't belong. Counts are safe: only one device is being accepted.",
                color = RsColor.Muted, textAlign = TextAlign.Center
            )
        }
    }
}

@Composable
private fun Header(vm: CounterViewModel, vehicleId: String, onCamera: () -> Unit) {
    var showUnbind by remember { mutableStateOf(false) }
    val context = androidx.compose.ui.platform.LocalContext.current
    Row(
        Modifier.fillMaxWidth().widthIn(max = 380.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        RsWordmark("Passenger Counter")
        Row(verticalAlignment = Alignment.CenterVertically) {
            // Phase 9b kiosk: screen-pin the app so it can't be swiped away or
            // backgrounded on the mounted phone. Unpin = system gesture (Back+Recents).
            TextButton(onClick = {
                runCatching { (context as? android.app.Activity)?.startLockTask() }
            }) { Text("Pin", color = RsColor.Muted, fontWeight = FontWeight.Bold) }
            TextButton(onClick = onCamera) { Text("Calibrate", color = RsColor.Navy, fontWeight = FontWeight.Bold) }
            TextButton(onClick = { showUnbind = true }) { Text(vehicleId, color = RsColor.Teal, fontWeight = FontWeight.Bold) }
        }
    }
    if (showUnbind) UnbindDialog(vm) { showUnbind = false }
}

@Composable
private fun StatusDot(active: Boolean) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(10.dp).clip(CircleShape).background(if (active) RsColor.TealBright else RsColor.Muted))
        Spacer(Modifier.width(6.dp))
        Text(if (active) "LIVE" else "IDLE", color = if (active) RsColor.TealBright else RsColor.Muted, fontSize = 12.sp, fontWeight = FontWeight.Bold)
    }
}

@Composable
private fun PrimaryButton(text: String, enabled: Boolean = true, onClick: () -> Unit) {
    Button(
        onClick = onClick, enabled = enabled,
        shape = RoundedCornerShape(12.dp),
        modifier = Modifier.fillMaxWidth().height(52.dp)
    ) { Text(text, fontWeight = FontWeight.Bold) }
}

@Composable
private fun UnbindDialog(vm: CounterViewModel, dismiss: () -> Unit) {
    var passcode by remember { mutableStateOf("") }
    var error by remember { mutableStateOf(false) }
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text("Change vehicle", color = RsColor.Navy, fontWeight = FontWeight.Bold) },
        text = {
            Column {
                Text("Enter the bind passcode to release this phone from its bus.", color = RsColor.Muted)
                Spacer(Modifier.height(12.dp))
                OutlinedTextField(
                    passcode, { passcode = it; error = false }, singleLine = true,
                    label = { Text("Passcode") }, isError = error,
                    visualTransformation = PasswordVisualTransformation()
                )
                if (error) Text("Wrong passcode.", color = RsColor.Error)
            }
        },
        confirmButton = {
            TextButton(onClick = {
                vm.unbind(passcode) { ok -> if (ok) dismiss() else error = true }
            }) { Text("Unbind", color = RsColor.Error, fontWeight = FontWeight.Bold) }
        },
        dismissButton = { TextButton(onClick = dismiss) { Text("Cancel", color = RsColor.Muted) } }
    )
}
