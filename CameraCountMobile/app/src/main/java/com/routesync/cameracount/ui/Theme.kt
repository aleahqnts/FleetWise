package com.routesync.cameracount.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** RouteSync suite palette (mirrors the mobile login + web dashboard theme). */
object RsColor {
    val Navy = Color(0xFF1B2A56)
    val Teal = Color(0xFF2E9E8F)
    val TealBright = Color(0xFF3AB5A4)
    val Mint1 = Color(0xFFEAF6F1)
    val Mint2 = Color(0xFFD6EDE6)
    val Mint3 = Color(0xFFC7E8DD)
    val FieldBorder = Color(0xFFD9DEE6)
    val Muted = Color(0xFF9AA3B2)
    val Error = Color(0xFFE74C3C)
    val CardWhite = Color(0xFFFFFFFF)
}

private val RsScheme = lightColorScheme(
    primary = RsColor.Teal,
    onPrimary = Color.White,
    secondary = RsColor.Navy,
    background = RsColor.Mint2,
    surface = RsColor.CardWhite,
    onSurface = RsColor.Navy,
    error = RsColor.Error,
    outline = RsColor.FieldBorder
)

@Composable
fun RsTheme(content: @Composable () -> Unit) =
    MaterialTheme(colorScheme = RsScheme, content = content)

/** Full-bleed mint gradient background (matches the mobile login-bg). */
@Composable
fun RsBackground(content: @Composable BoxScope.() -> Unit) {
    Box(
        Modifier
            .fillMaxSize()
            .background(
                Brush.linearGradient(listOf(RsColor.Mint1, RsColor.Mint2, RsColor.Mint3))
            ),
        content = content
    )
}

/** Two-tone "RouteSync" wordmark + tagline, same mark as the driver app. */
@Composable
fun RsWordmark(tagline: String) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Row {
            Text("Route", fontSize = 28.sp, fontWeight = FontWeight.ExtraBold, color = RsColor.Navy)
            Text("Sync", fontSize = 28.sp, fontWeight = FontWeight.ExtraBold, color = RsColor.Teal)
        }
        Text(tagline, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = RsColor.Muted)
    }
}

/** White rounded card, same shadow/radius language as the mobile login card. */
@Composable
fun RsCard(content: @Composable ColumnScope.() -> Unit) {
    Surface(
        shape = RoundedCornerShape(18.dp),
        color = RsColor.CardWhite,
        shadowElevation = 10.dp,
        modifier = Modifier.fillMaxWidth().widthIn(max = 380.dp)
    ) {
        Column(Modifier.padding(24.dp), content = content)
    }
}
