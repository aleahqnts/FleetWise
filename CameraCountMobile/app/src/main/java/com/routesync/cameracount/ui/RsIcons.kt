package com.routesync.cameracount.ui

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.unit.dp

// Icons are drawn here rather than taken from material-icons-extended. That artifact
// carries every Material glyph, and this app builds with minification disabled, so the
// whole set would be packaged for the sake of one button. Stroke-only, so they stay
// legible against the black camera preview at any size.
private fun strokeIcon(name: String, vararg paths: String): ImageVector =
    ImageVector.Builder(
        name = name,
        defaultWidth = 24.dp,
        defaultHeight = 24.dp,
        viewportWidth = 24f,
        viewportHeight = 24f
    ).apply {
        paths.forEach { d ->
            addPath(
                pathData = PathParser().parsePathString(d).toNodes(),
                // Icon() tints the whole painter, so this colour is only a base.
                stroke = SolidColor(Color.White),
                strokeLineWidth = 1.7f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round
            )
        }
    }.build()

object RsIcons {
    /**
     * Swap glyph: two arrows following each other around a rounded square.
     *
     * The ring is split into halves with a gap on each side, so the pair reads as two
     * arrows rather than one unbroken box.
     */
    val Cameraswitch: ImageVector by lazy {
        strokeIcon(
            "Cameraswitch",
            // Upper arrow: up the left side, over the top, down the right, head at the end.
            "M5,10.2 L5,8 A3,3 0 0 1 8,5 L16,5 A3,3 0 0 1 19,8 L19,10.2",
            "M17.9,9.1 L19,10.2 L20.1,9.1",
            // Lower arrow: the same path rotated 180 degrees, closing the loop.
            "M19,13.8 L19,16 A3,3 0 0 1 16,19 L8,19 A3,3 0 0 1 5,16 L5,13.8",
            "M6.1,14.9 L5,13.8 L3.9,14.9"
        )
    }
}
