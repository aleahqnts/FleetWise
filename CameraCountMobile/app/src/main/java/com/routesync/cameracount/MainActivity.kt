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
import com.routesync.cameracount.ui.*

/**
 * CameraCount Mobile — RouteSync's camera-based passenger counter.
 * Phase 1: vehicle bind + trip poll + fake +1 counter proving the DB bridge.
 * UI mirrors the RouteSync driver-app / dashboard theme (see ui/Theme.kt).
 */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { RsTheme { Root() } }
    }
}

@Composable
fun Root(vm: CounterViewModel = viewModel()) {
    var showCamera by remember { mutableStateOf(false) }
    if (showCamera) {
        CameraScreen(onClose = { showCamera = false })
        return
    }
    RsBackground {
        Column(
            Modifier.fillMaxSize().padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center
        ) {
            when (val s = vm.state.collectAsState().value) {
                is CounterViewModel.UiState.NeedsSetup -> SetupCard(onBind = vm::bind)
                is CounterViewModel.UiState.Waiting -> WaitingCard(vm, s, onCamera = { showCamera = true })
                is CounterViewModel.UiState.Counting -> CountingCard(vm, s, onCamera = { showCamera = true })
            }
        }
    }
}

@Composable
private fun SetupCard(onBind: (String, String) -> Unit) {
    var vehicle by remember { mutableStateOf("") }
    var passcode by remember { mutableStateOf("") }
    RsWordmark("Passenger Counter")
    Spacer(Modifier.height(24.dp))
    RsCard {
        Text("Set up this device", fontSize = 20.sp, fontWeight = FontWeight.Bold, color = RsColor.Navy)
        Spacer(Modifier.height(4.dp))
        Text("Bind this phone to the bus it is mounted in.", color = RsColor.Muted)
        Spacer(Modifier.height(20.dp))
        OutlinedTextField(
            vehicle, { vehicle = it.uppercase() }, singleLine = true,
            label = { Text("Vehicle ID (e.g. V001)") }, modifier = Modifier.fillMaxWidth()
        )
        Spacer(Modifier.height(12.dp))
        OutlinedTextField(
            passcode, { passcode = it }, singleLine = true,
            label = { Text("Bind passcode") }, modifier = Modifier.fillMaxWidth(),
            visualTransformation = PasswordVisualTransformation()
        )
        Spacer(Modifier.height(20.dp))
        PrimaryButton(
            "Bind vehicle",
            enabled = vehicle.isNotBlank() && passcode.isNotBlank()
        ) { onBind(vehicle, passcode) }
    }
}

@Composable
private fun WaitingCard(vm: CounterViewModel, s: CounterViewModel.UiState.Waiting, onCamera: () -> Unit) {
    Header(vm, s.vehicleId, onCamera)
    Spacer(Modifier.height(20.dp))
    RsCard {
        Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
            StatusDot(active = false)
            Spacer(Modifier.height(12.dp))
            Text("Waiting for trip", fontSize = 20.sp, fontWeight = FontWeight.Bold, color = RsColor.Navy)
            Spacer(Modifier.height(8.dp))
            Text(
                "Counting starts automatically when the driver starts a trip for ${s.vehicleId}.",
                color = RsColor.Muted, textAlign = TextAlign.Center
            )
            s.lastError?.let {
                Spacer(Modifier.height(12.dp))
                Text("Last poll error: $it", color = RsColor.Error, textAlign = TextAlign.Center)
            }
        }
    }
}

@Composable
private fun CountingCard(vm: CounterViewModel, s: CounterViewModel.UiState.Counting, onCamera: () -> Unit) {
    Header(vm, s.vehicleId, onCamera)
    Spacer(Modifier.height(20.dp))
    RsCard {
        Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
            StatusDot(active = true)
            Spacer(Modifier.height(6.dp))
            Text("Counting  ·  ${s.tripId}", color = RsColor.Muted, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(12.dp))
            Text("${s.count}", fontSize = 72.sp, fontWeight = FontWeight.ExtraBold, color = RsColor.Teal)
            Text("passengers boarded", color = RsColor.Navy)
            Spacer(Modifier.height(24.dp))
            PrimaryButton("+1  (fake camera event)") { vm.increment() }
            Spacer(Modifier.height(14.dp))
            Text(
                if (s.lastFlushOk) "Synced · count + heartbeat every 5s"
                else "Sync retrying — will catch up",
                color = if (s.lastFlushOk) RsColor.Teal else RsColor.Error,
                fontSize = 13.sp
            )
        }
    }
}

@Composable
private fun Header(vm: CounterViewModel, vehicleId: String, onCamera: () -> Unit) {
    var showUnbind by remember { mutableStateOf(false) }
    Row(
        Modifier.fillMaxWidth().widthIn(max = 380.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        RsWordmark("Passenger Counter")
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onCamera) { Text("Camera", color = RsColor.Navy, fontWeight = FontWeight.Bold) }
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
