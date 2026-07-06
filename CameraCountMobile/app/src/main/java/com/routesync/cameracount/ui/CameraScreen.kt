package com.routesync.cameracount.ui

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.routesync.cameracount.CounterViewModel
import com.routesync.cameracount.camera.DetectorAnalyzer
import com.routesync.cameracount.camera.LineCrossCounter
import com.routesync.cameracount.camera.PersonTracker
import com.routesync.cameracount.camera.YoloDetector
import com.routesync.cameracount.data.Prefs
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import java.util.concurrent.Executors

/** Immutable per-frame snapshot for the overlay (built on the analyzer thread). */
private data class OverlayBox(val l: Float, val t: Float, val r: Float, val b: Float, val counted: Boolean)

/**
 * Phase 3/4/5 camera surface.
 * - [vm] != null: COUNTING mode — ByteTrack + line-cross feed vm.increment(); uses the
 *   per-device SAVED line. Trip end (vm state leaves Counting) tears this down from Root.
 * - [calibrate] = true: CALIBRATION — live preview + boxes, DRAG the line onto the real
 *   doorway pathway, flip boarding direction, Save -> persisted (DataStore), survives
 *   restarts. Recalibrate any time the mount shifts; no rebuild needed.
 * - neither: plain detection preview.
 */
@Composable
fun CameraScreen(
    vm: CounterViewModel? = null,
    calibrate: Boolean = false,
    onClose: (() -> Unit)? = null
) {
    val context = LocalContext.current
    var granted by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) ==
                PackageManager.PERMISSION_GRANTED
        )
    }
    val ask = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        granted = it
    }

    LaunchedEffect(Unit) { if (!granted) ask.launch(Manifest.permission.CAMERA) }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        when {
            !granted -> CenterMsg("Camera permission needed.\nTap to grant.") {
                ask.launch(Manifest.permission.CAMERA)
            }
            else -> {
                val detector = remember { YoloDetector.tryCreate(context) }
                if (detector == null) {
                    CenterMsg(
                        "Model missing.\n\nExport YOLO11n and place it at\napp/src/main/assets/${YoloDetector.MODEL_ASSET}\n\nSee assets/README.txt for the one-line export.",
                        null
                    )
                } else {
                    DetectionSurface(detector, vm, calibrate, onClose)
                }
            }
        }
        onClose?.let {
            Button(
                onClick = it,
                modifier = Modifier.align(Alignment.TopEnd).padding(16.dp)
            ) { Text("Close") }
        }
    }
}

@Composable
private fun DetectionSurface(
    detector: YoloDetector,
    vm: CounterViewModel?,
    calibrate: Boolean = false,
    onClose: (() -> Unit)? = null
) {
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current
    val scope = rememberCoroutineScope()
    val prefs = remember { Prefs(context) }

    // Calibrated line = two endpoints (any angle) + boarding side. Loaded once; the two
    // drag handles edit A/B live in calibrate mode.
    var ax by remember { mutableFloatStateOf(Prefs.DEF_AX) }
    var ay by remember { mutableFloatStateOf(Prefs.DEF_AY) }
    var bx by remember { mutableFloatStateOf(Prefs.DEF_BX) }
    var by by remember { mutableFloatStateOf(Prefs.DEF_BY) }
    var inwardSign by remember { mutableIntStateOf(Prefs.DEF_INWARD_SIGN) }
    var lineLoaded by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        val cal = prefs.lineCalibration.first()
        ax = cal.ax; ay = cal.ay; bx = cal.bx; by = cal.by; inwardSign = cal.inwardSign
        lineLoaded = true
    }

    var boxes by remember { mutableStateOf<List<OverlayBox>>(emptyList()) }
    var frameW by remember { mutableIntStateOf(1) }
    var frameH by remember { mutableIntStateOf(1) }
    var inferMs by remember { mutableLongStateOf(0L) }
    var fps by remember { mutableIntStateOf(0) }
    var frames by remember { mutableIntStateOf(0) }
    var windowStart by remember { mutableLongStateOf(System.currentTimeMillis()) }
    var frameError by remember { mutableStateOf<String?>(null) }

    val counting = vm != null

    // Mid-trip line adjust: mount slips are fixed on the spot, no passcode — a driver
    // wrestling a passcode dialog while boarding continues would just give up on it.
    var adjusting by remember { mutableStateOf(false) }
    val editingLine = calibrate || adjusting

    // Dim mode: nobody in frame for 60s -> near-black overlay (heat + burn-in relief).
    // Detection keeps running underneath; a person in frame or a tap wakes the screen.
    var lastPersonAt by remember { mutableLongStateOf(System.currentTimeMillis()) }
    var dimmed by remember { mutableStateOf(false) }
    if (counting) LaunchedEffect(Unit) {
        while (true) {
            kotlinx.coroutines.delay(5_000)
            dimmed = System.currentTimeMillis() - lastPersonAt > 60_000 && !editingLine
        }
    }

    // Charging watch: a dashboard phone that isn't charging is a counter on a timer.
    var charging by remember { mutableStateOf(true) }
    DisposableEffect(Unit) {
        val receiver = object : android.content.BroadcastReceiver() {
            override fun onReceive(c: android.content.Context?, i: android.content.Intent?) {
                charging = (i?.getIntExtra(android.os.BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0
            }
        }
        val sticky = context.registerReceiver(
            receiver, android.content.IntentFilter(android.content.Intent.ACTION_BATTERY_CHANGED)
        )
        charging = (sticky?.getIntExtra(android.os.BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0
        onDispose { context.unregisterReceiver(receiver) }
    }

    // Phase 6: dashboard phone must never sleep mid-trip (or mid-calibration).
    val view = androidx.compose.ui.platform.LocalView.current
    DisposableEffect(Unit) {
        view.keepScreenOn = true
        onDispose { view.keepScreenOn = false }
    }

    // Phase 6 thermal guard: closed bus + sun = throttle before the OS kills us.
    // SEVERE -> infer every 2nd frame, CRITICAL+ -> every 3rd. Listener-driven (no
    // per-frame binder calls); API 29+, older devices just run unthrottled.
    var thermalSkip by remember { mutableIntStateOf(1) }
    DisposableEffect(Unit) {
        if (android.os.Build.VERSION.SDK_INT >= 29) {
            val pm = context.getSystemService(android.os.PowerManager::class.java)
            val listener = android.os.PowerManager.OnThermalStatusChangedListener { status ->
                thermalSkip = when {
                    status >= android.os.PowerManager.THERMAL_STATUS_CRITICAL -> 3
                    status >= android.os.PowerManager.THERMAL_STATUS_SEVERE -> 2
                    else -> 1
                }
            }
            pm.addThermalStatusListener(listener)
            onDispose { pm.removeThermalStatusListener(listener) }
        } else onDispose { }
    }

    val tracker = remember { PersonTracker() }
    val lineCounter = remember { LineCrossCounter() }
    LaunchedEffect(ax, ay, bx, by, inwardSign) {
        lineCounter.ax = ax; lineCounter.ay = ay
        lineCounter.bx = bx; lineCounter.by = by
        lineCounter.inwardSign = inwardSign
    }

    val executor = remember { Executors.newSingleThreadExecutor() }
    DisposableEffect(Unit) {
        onDispose {
            // Order matters: stop frames FIRST, then stop the worker, then free the
            // interpreter — closing native memory under a live frame = SIGSEGV.
            runCatching { ProcessCameraProvider.getInstance(context).get().unbindAll() }
            executor.shutdown()
            detector.close() // synchronized with detect(): waits out any in-flight frame
        }
    }

    Box(Modifier.fillMaxSize()) {
        AndroidView(
            modifier = Modifier.fillMaxSize(),
            factory = { ctx ->
                val view = PreviewView(ctx).apply { scaleType = PreviewView.ScaleType.FIT_CENTER }
                val providerFuture = ProcessCameraProvider.getInstance(ctx)
                providerFuture.addListener({
                    val provider = providerFuture.get()
                    val preview = Preview.Builder().build()
                        .also { it.surfaceProvider = view.surfaceProvider }
                    val analysis = ImageAnalysis.Builder()
                        .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                        // RGBA out: toBitmap() on YUV frames is unsupported on some devices
                        // (every frame throws -> 0 fps). RGBA conversion is built-in.
                        .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_RGBA_8888)
                        .build()

                    val front = provider.hasCamera(CameraSelector.DEFAULT_FRONT_CAMERA)
                    val selector =
                        if (front) CameraSelector.DEFAULT_FRONT_CAMERA
                        else CameraSelector.DEFAULT_BACK_CAMERA

                    analysis.setAnalyzer(
                        executor,
                        DetectorAnalyzer(
                            detector, mirrored = front,
                            onError = { frameError = it },
                            throttle = { thermalSkip }
                        ) { dets, w, h, ms ->
                            vm?.noteFrame() // stall guard: silence -> heartbeat stops -> manual fallback
                            if (dets.isNotEmpty()) lastPersonAt = System.currentTimeMillis()
                            boxes = if (counting) {
                                // Real counting path: tracker IDs -> line-cross -> count.
                                // (crossings ignored until the saved line is loaded, and
                                // while the line itself is being dragged mid-trip)
                                val tracks = tracker.update(dets)
                                val crossings = lineCounter.process(tracks)
                                if (lineLoaded && !adjusting) repeat(crossings) { vm!!.increment() }
                                tracks.map {
                                    OverlayBox(it.box.left, it.box.top, it.box.right, it.box.bottom, it.counted)
                                }
                            } else {
                                dets.filter { it.score >= YoloDetector.HIGH_CONF }
                                    .map { OverlayBox(it.box.left, it.box.top, it.box.right, it.box.bottom, false) }
                            }
                            frameW = w; frameH = h; inferMs = ms; frameError = null
                            frames++
                            val now = System.currentTimeMillis()
                            if (now - windowStart >= 1000) {
                                fps = frames; frames = 0; windowStart = now
                            }
                        }
                    )
                    provider.unbindAll()
                    provider.bindToLifecycle(lifecycle, selector, preview, analysis)
                }, ContextCompat.getMainExecutor(ctx))
                view
            }
        )

        // Boxes + counting line: frame-normalized coords mapped into the FIT_CENTER rect.
        // In calibrate mode, drag grabs whichever endpoint handle (A/B) is nearer the touch.
        var activeHandle by remember { mutableIntStateOf(-1) } // 0=A, 1=B
        val canvasModifier = if (editingLine) {
            Modifier.fillMaxSize().pointerInput(frameW, frameH) {
                fun norm(px: Float, py: Float): Pair<Float, Float> {
                    val scale = minOf(size.width.toFloat() / frameW, size.height.toFloat() / frameH)
                    val cw = frameW * scale; val ch = frameH * scale
                    val ox = (size.width - cw) / 2f; val oy = (size.height - ch) / 2f
                    return ((px - ox) / cw).coerceIn(0f, 1f) to ((py - oy) / ch).coerceIn(0f, 1f)
                }
                detectDragGestures(
                    onDragStart = { pos ->
                        val (nx, ny) = norm(pos.x, pos.y)
                        val da = (nx - ax) * (nx - ax) + (ny - ay) * (ny - ay)
                        val db = (nx - bx) * (nx - bx) + (ny - by) * (ny - by)
                        activeHandle = if (da <= db) 0 else 1
                    },
                    onDragEnd = { activeHandle = -1 },
                    onDrag = { change, _ ->
                        val (nx, ny) = norm(change.position.x, change.position.y)
                        if (activeHandle == 0) { ax = nx; ay = ny } else { bx = nx; by = ny }
                    }
                )
            }
        } else Modifier.fillMaxSize()
        Canvas(canvasModifier) {
            val scale = minOf(size.width / frameW, size.height / frameH)
            val cw = frameW * scale
            val ch = frameH * scale
            val ox = (size.width - cw) / 2f
            val oy = (size.height - ch) / 2f
            fun pt(nx: Float, ny: Float) = Offset(ox + nx * cw, oy + ny * ch)
            boxes.forEach { b ->
                drawRect(
                    color = if (b.counted) Color(0xFF9AA3B2) else RsColor.TealBright,
                    topLeft = Offset(ox + b.l * cw, oy + b.t * ch),
                    size = Size((b.r - b.l) * cw, (b.b - b.t) * ch),
                    style = Stroke(width = 4f)
                )
            }
            if (counting || calibrate) {
                val pa = pt(ax, ay)
                val pb = pt(bx, by)
                drawLine(
                    color = Color(0xFFFFC94D), start = pa, end = pb,
                    strokeWidth = if (editingLine) 8f else 5f,
                    pathEffect = PathEffect.dashPathEffect(floatArrayOf(28f, 18f))
                )
                // Inward arrow: perpendicular to the line at its midpoint, pointing to the
                // boarding side (inwardSign). dx,dy = line dir; normal = (-dy, dx).
                val mx = (pa.x + pb.x) / 2f; val my = (pa.y + pb.y) / 2f
                var ndx = -(pb.y - pa.y); var ndy = (pb.x - pa.x)
                val len = kotlin.math.hypot(ndx, ndy).coerceAtLeast(1f)
                ndx = ndx / len * inwardSign; ndy = ndy / len * inwardSign
                val tip = Offset(mx + ndx * 60f, my + ndy * 60f)
                drawLine(Color(0xFFFFC94D), Offset(mx, my), tip, strokeWidth = 6f)
                drawCircle(Color(0xFFFFC94D), 10f, tip)
                if (editingLine) {
                    // Grab handles.
                    drawCircle(Color.White, 26f, pa); drawCircle(Color(0xFFFFC94D), 18f, pa)
                    drawCircle(Color.White, 26f, pb); drawCircle(Color(0xFFFFC94D), 18f, pb)
                }
            }
        }

        Column(
            Modifier.align(Alignment.TopStart).padding(16.dp)
                .background(Color(0xAA000000)).padding(horizontal = 10.dp, vertical = 6.dp)
        ) {
            Text("persons: ${boxes.size}", color = RsColor.TealBright, fontWeight = FontWeight.Bold)
            Text(
                "$fps fps · ${inferMs}ms · ${if (detector.usingGpu) "GPU" else "CPU"}",
                color = Color.White, fontSize = 12.sp
            )
            frameError?.let {
                Text(it, color = Color(0xFFFF6B6B), fontSize = 11.sp)
            }
        }

        // Calibration controls: drag hint, boarding-direction flip, save. Shown both for
        // the standalone calibrate screen and the mid-trip adjust overlay.
        if (editingLine) {
            Column(
                Modifier.align(Alignment.BottomCenter).padding(bottom = 28.dp),
                horizontalAlignment = Alignment.CenterHorizontally
            ) {
                Text(
                    "Drag the two dots to place the line across the pathway",
                    color = Color.White, fontSize = 13.sp,
                    modifier = Modifier.background(Color(0xAA000000)).padding(horizontal = 10.dp, vertical = 4.dp)
                )
                Spacer(Modifier.height(12.dp))
                Row {
                    OutlinedButton(onClick = { inwardSign = -inwardSign }) {
                        Text("Flip boarding side", color = Color.White)
                    }
                    Spacer(Modifier.width(12.dp))
                    Button(onClick = {
                        scope.launch {
                            prefs.saveLine(ax, ay, bx, by, inwardSign)
                            if (adjusting) adjusting = false else onClose?.invoke()
                        }
                    }) { Text("Save line", fontWeight = FontWeight.Bold) }
                }
                if (adjusting) {
                    Spacer(Modifier.height(8.dp))
                    TextButton(onClick = {
                        // Cancel: reload the saved line, drop the drag.
                        scope.launch {
                            val cal = prefs.lineCalibration.first()
                            ax = cal.ax; ay = cal.ay; bx = cal.bx; by = cal.by; inwardSign = cal.inwardSign
                            adjusting = false
                        }
                    }) { Text("Cancel", color = Color.White) }
                }
            }
        }

        // Mid-trip line adjust entry point (counting continues; crossings pause while dragging).
        if (counting && !editingLine) {
            OutlinedButton(
                onClick = { adjusting = true; lastPersonAt = System.currentTimeMillis() },
                modifier = Modifier.align(Alignment.TopEnd).padding(16.dp)
            ) { Text("Adjust line", color = Color.White, fontSize = 12.sp) }
        }

        // Not-charging warning: the counter dies with the battery. Sits just above the HUD.
        if (counting && !charging) {
            Text(
                "⚡ Not charging — plug the phone in",
                color = Color(0xFFFFC94D), fontSize = 13.sp, fontWeight = FontWeight.Bold,
                modifier = Modifier.align(Alignment.BottomCenter).padding(bottom = 176.dp)
                    .clip(RoundedCornerShape(8.dp)).background(Color(0xCC000000))
                    .padding(horizontal = 12.dp, vertical = 6.dp)
            )
        }

        // Counting HUD: live count + trip + sync state, fed by the ViewModel.
        if (vm != null && !adjusting) {
            val s = vm.state.collectAsState().value
            if (s is CounterViewModel.UiState.Counting) {
                // Each +1 flashes the number white and pops it slightly — glanceable trust.
                val flash = remember { androidx.compose.animation.core.Animatable(0f) }
                LaunchedEffect(s.count) {
                    if (s.count > 0) {
                        flash.snapTo(1f)
                        flash.animateTo(0f, androidx.compose.animation.core.tween(650))
                    }
                }
                Column(
                    Modifier.align(Alignment.BottomCenter).padding(bottom = 32.dp)
                        .clip(RoundedCornerShape(18.dp))
                        .background(Color(0xCC10231F))
                        .padding(horizontal = 28.dp, vertical = 14.dp),
                    horizontalAlignment = Alignment.CenterHorizontally
                ) {
                    Text(
                        "${s.count}",
                        color = androidx.compose.ui.graphics.lerp(RsColor.TealBright, Color.White, flash.value),
                        fontSize = 56.sp, fontWeight = FontWeight.ExtraBold,
                        modifier = Modifier.graphicsLayer {
                            val sc = 1f + 0.14f * flash.value
                            scaleX = sc; scaleY = sc
                        }
                    )
                    Text("passengers boarded", color = Color.White, fontSize = 13.sp)
                    Spacer(Modifier.height(4.dp))
                    Text(
                        "${s.tripId} · " + when {
                            s.cameraStalled -> "camera stalled — driver counting"
                            s.lastFlushOk -> "synced"
                            else -> "sync retrying"
                        },
                        color = if (s.lastFlushOk && !s.cameraStalled) RsColor.TealBright else Color(0xFFFF6B6B),
                        fontSize = 12.sp
                    )
                }
            }
        }

        // Dim overlay (topmost): idle bus stop / long empty stretch -> near-black screen
        // saves heat + battery + OLED. Detection still runs; a person or a tap wakes it.
        if (counting && dimmed) {
            val dimCount = (vm?.state?.collectAsState()?.value as? CounterViewModel.UiState.Counting)?.count
            Column(
                Modifier.fillMaxSize().background(Color(0xF2000000))
                    .pointerInput(Unit) {
                        detectTapGestures { lastPersonAt = System.currentTimeMillis(); dimmed = false }
                    },
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                dimCount?.let {
                    Text("$it", color = Color(0x559AE0D4), fontSize = 44.sp, fontWeight = FontWeight.Bold)
                }
                Spacer(Modifier.height(8.dp))
                Text("counting · screen resting", color = Color(0x44FFFFFF), fontSize = 12.sp)
            }
        }
    }
}

@Composable
private fun CenterMsg(text: String, onTap: (() -> Unit)?) {
    Column(
        Modifier.fillMaxSize().padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Text(text, color = Color.White, fontSize = 15.sp)
        onTap?.let {
            Spacer(Modifier.height(16.dp))
            Button(onClick = it) { Text("Grant camera access") }
        }
    }
}
