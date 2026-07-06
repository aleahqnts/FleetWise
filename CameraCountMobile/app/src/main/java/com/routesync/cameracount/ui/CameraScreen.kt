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
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.routesync.cameracount.camera.DetectorAnalyzer
import com.routesync.cameracount.camera.YoloDetector
import java.util.concurrent.Executors

/**
 * Phase 3: live front-camera preview with YOLO person boxes + fps. Pure detection demo —
 * counting (line-cross) arrives in Phase 4 on top of exactly this pipeline.
 */
@Composable
fun CameraScreen(onClose: () -> Unit) {
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
                    DetectionPreview(detector)
                }
            }
        }
        Button(
            onClick = onClose,
            modifier = Modifier.align(Alignment.TopEnd).padding(16.dp)
        ) { Text("Close") }
    }
}

@Composable
private fun DetectionPreview(detector: YoloDetector) {
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current

    var dets by remember { mutableStateOf<List<YoloDetector.Det>>(emptyList()) }
    var frameW by remember { mutableIntStateOf(1) }
    var frameH by remember { mutableIntStateOf(1) }
    var inferMs by remember { mutableLongStateOf(0L) }
    var fps by remember { mutableIntStateOf(0) }
    var frames by remember { mutableIntStateOf(0) }
    var windowStart by remember { mutableLongStateOf(System.currentTimeMillis()) }
    var frameError by remember { mutableStateOf<String?>(null) }

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
                    DetectorAnalyzer(detector, mirrored = front, onError = { frameError = it }) { d, w, h, ms ->
                        dets = d; frameW = w; frameH = h; inferMs = ms; frameError = null
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

    // Boxes: frame-normalized coords mapped into the FIT_CENTER content rect.
    Canvas(Modifier.fillMaxSize()) {
        val scale = minOf(size.width / frameW, size.height / frameH)
        val cw = frameW * scale
        val ch = frameH * scale
        val ox = (size.width - cw) / 2f
        val oy = (size.height - ch) / 2f
        dets.forEach { d ->
            drawRect(
                color = RsColor.TealBright,
                topLeft = Offset(ox + d.box.left * cw, oy + d.box.top * ch),
                size = Size(d.box.width() * cw, d.box.height() * ch),
                style = Stroke(width = 4f)
            )
        }
    }

    Column(
        Modifier.align(Alignment.TopStart).padding(16.dp)
            .background(Color(0xAA000000)).padding(horizontal = 10.dp, vertical = 6.dp)
    ) {
        Text("persons: ${dets.size}", color = RsColor.TealBright, fontWeight = FontWeight.Bold)
        Text(
            "$fps fps · ${inferMs}ms · ${if (detector.usingGpu) "GPU" else "CPU"}",
            color = Color.White, fontSize = 12.sp
        )
        frameError?.let {
            Text(it, color = Color(0xFFFF6B6B), fontSize = 11.sp)
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
