package com.routesync.cameracount

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.routesync.cameracount.data.SupabaseApi
import kotlinx.coroutines.launch

/**
 * CameraCount Mobile — RouteSync's camera-based passenger counter.
 * Phase 0: scaffold + DB connectivity smoke test.
 * Phase 1 adds: vehicle bind (Setup), 3-5s trip poll, fake +1 counter, 5s PATCH loop.
 */
class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { MaterialTheme { Phase0Screen() } }
    }
}

@Composable
fun Phase0Screen() {
    val scope = rememberCoroutineScope()
    var status by remember { mutableStateOf("Not checked") }

    Column(
        modifier = Modifier.fillMaxSize().padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Text("CameraCount", style = MaterialTheme.typography.headlineMedium)
        Text("RouteSync passenger counter — Phase 0", style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(32.dp))
        Text("DB link: $status")
        Spacer(Modifier.height(16.dp))
        Button(onClick = {
            status = "Checking..."
            scope.launch {
                status = try {
                    if (SupabaseApi.ping()) "OK — Supabase reachable" else "FAILED — non-2xx"
                } catch (e: Exception) {
                    "FAILED — ${e.message}"
                }
            }
        }) { Text("Test DB connection") }
    }
}
