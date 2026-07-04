package com.routesync.cameracount

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel

/**
 * CameraCount Mobile — RouteSync's camera-based passenger counter.
 * Phase 1: vehicle bind + trip poll + fake +1 counter proving the DB bridge.
 * Phase 3+ replaces the +1 button with the camera pipeline; screens/loops stay.
 */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { MaterialTheme { Root() } }
    }
}

@Composable
fun Root(vm: CounterViewModel = viewModel()) {
    when (val s = vm.state.collectAsState().value) {
        is CounterViewModel.UiState.NeedsSetup -> SetupScreen(onBind = vm::bind)
        is CounterViewModel.UiState.Waiting -> StatusScaffold(vm, s.vehicleId) {
            Text("Waiting for trip…", style = MaterialTheme.typography.headlineSmall)
            Spacer(Modifier.height(8.dp))
            Text("Counting starts automatically when the driver starts a trip for ${s.vehicleId}.")
            s.lastError?.let {
                Spacer(Modifier.height(12.dp))
                Text("Last poll error: $it", color = MaterialTheme.colorScheme.error)
            }
        }
        is CounterViewModel.UiState.Counting -> StatusScaffold(vm, s.vehicleId) {
            Text("Counting — ${s.tripId}", style = MaterialTheme.typography.titleMedium)
            Spacer(Modifier.height(16.dp))
            Text("${s.count}", style = MaterialTheme.typography.displayLarge)
            Text("passengers boarded")
            Spacer(Modifier.height(24.dp))
            // Phase-1 stand-in for camera line-cross events.
            Button(onClick = vm::increment, modifier = Modifier.height(64.dp)) {
                Text("+1 (fake camera event)")
            }
            Spacer(Modifier.height(16.dp))
            Text(
                if (s.lastFlushOk) "Sync: OK (count + heartbeat every 5s)"
                else "Sync: retrying — will catch up",
                color = if (s.lastFlushOk) MaterialTheme.colorScheme.primary
                else MaterialTheme.colorScheme.error
            )
        }
    }
}

@Composable
private fun SetupScreen(onBind: (String, String) -> Unit) {
    var vehicle by remember { mutableStateOf("") }
    var passcode by remember { mutableStateOf("") }
    Column(
        Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Text("CameraCount Setup", style = MaterialTheme.typography.headlineMedium)
        Spacer(Modifier.height(8.dp))
        Text("Bind this phone to the bus it is mounted in.")
        Spacer(Modifier.height(24.dp))
        OutlinedTextField(vehicle, { vehicle = it }, label = { Text("Vehicle ID (e.g. BUS-01)") })
        Spacer(Modifier.height(12.dp))
        OutlinedTextField(
            passcode, { passcode = it }, label = { Text("Bind passcode") },
            visualTransformation = PasswordVisualTransformation()
        )
        Spacer(Modifier.height(24.dp))
        Button(
            onClick = { onBind(vehicle, passcode) },
            enabled = vehicle.isNotBlank() && passcode.isNotBlank()
        ) { Text("Bind vehicle") }
    }
}

@Composable
private fun StatusScaffold(vm: CounterViewModel, vehicleId: String, content: @Composable ColumnScope.() -> Unit) {
    var showUnbind by remember { mutableStateOf(false) }
    Column(
        Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text("CameraCount — $vehicleId", style = MaterialTheme.typography.titleMedium)
            TextButton(onClick = { showUnbind = true }) { Text("Change vehicle") }
        }
        Spacer(Modifier.height(48.dp))
        content()
    }
    if (showUnbind) UnbindDialog(vm) { showUnbind = false }
}

@Composable
private fun UnbindDialog(vm: CounterViewModel, dismiss: () -> Unit) {
    var passcode by remember { mutableStateOf("") }
    var error by remember { mutableStateOf(false) }
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text("Change vehicle") },
        text = {
            Column {
                Text("Enter the bind passcode to release this phone from its bus.")
                Spacer(Modifier.height(12.dp))
                OutlinedTextField(
                    passcode, { passcode = it; error = false }, label = { Text("Passcode") },
                    isError = error,
                    visualTransformation = PasswordVisualTransformation()
                )
                if (error) Text("Wrong passcode.", color = MaterialTheme.colorScheme.error)
            }
        },
        confirmButton = {
            TextButton(onClick = {
                vm.unbind(passcode) { ok -> if (ok) dismiss() else error = true }
            }) { Text("Unbind") }
        },
        dismissButton = { TextButton(onClick = dismiss) { Text("Cancel") } }
    )
}
