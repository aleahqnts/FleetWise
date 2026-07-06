package com.routesync.cameracount.camera

import android.graphics.RectF

/**
 * ByteTrack-lite: two-stage IoU association + constant-velocity prediction.
 *
 * Stage 1 matches tracks to HIGH-confidence detections; stage 2 rescues still-unmatched
 * tracks with LOW-confidence detections (the ByteTrack trick — a person half-hidden by
 * the door frame drops to 0.3 conf but keeps their ID instead of respawning as a new one,
 * which would double-count). Only high-conf detections can BIRTH a track, and a track
 * must live MIN_HITS frames before it counts — one-frame ghosts (hands, glare) never
 * become countable.
 *
 * All coords are frame-normalized (0..1), same space DetectorAnalyzer emits.
 */
class PersonTracker {

    class Track internal constructor(val id: Int, var box: RectF, var score: Float) {
        var vx = 0f
        var vy = 0f
        var hits = 1
        var misses = 0
        /** set once the track crossed the line inward — a person counts exactly once */
        var counted = false
        /** which side of the counting line this track was on last frame; 0 = undecided */
        var prevSide = 0
        /**
         * side the track was BORN on (first frame it cleared the dead band). Only tracks
         * born OUTWARD may ever count: someone already past the line who steps back and
         * returns, a driver near the line, or a track popping up mid-frame can never +1.
         */
        var originSide = 0
        val cx get() = box.centerX()
        val cy get() = box.centerY()
        val confirmed get() = hits >= MIN_HITS
    }

    companion object {
        private const val IOU_GATE = 0.25f
        private const val MAX_MISSES = 12 // ~1.2s at 10fps before a track dies
        private const val MIN_HITS = 3
    }

    private var nextId = 1
    private val tracks = mutableListOf<Track>()

    /** Feed one frame's detections; returns live confirmed tracks (for counting + overlay). */
    fun update(dets: List<YoloDetector.Det>): List<Track> {
        // Predict every track one frame forward.
        for (t in tracks) t.box.offset(t.vx, t.vy)

        val unmatchedTracks = tracks.toMutableList()
        val high = dets.filter { it.score >= YoloDetector.HIGH_CONF }.toMutableList()
        val low = dets.filter { it.score < YoloDetector.HIGH_CONF }.toMutableList()

        associate(unmatchedTracks, high)
        associate(unmatchedTracks, low)

        // Whatever's still unmatched ages; too old -> gone.
        val dead = ArrayList<Track>()
        for (t in unmatchedTracks) if (++t.misses > MAX_MISSES) dead.add(t)
        tracks.removeAll(dead)

        // Leftover high-conf detections become new tracks (low-conf never births one).
        for (d in high) tracks.add(Track(nextId++, RectF(d.box), d.score))

        return tracks.filter { it.confirmed && it.misses == 0 }
    }

    /** Greedy best-IoU matching; simple and plenty at door distances. */
    private fun associate(
        unmatchedTracks: MutableList<Track>,
        unmatchedDets: MutableList<YoloDetector.Det>
    ) {
        while (unmatchedTracks.isNotEmpty() && unmatchedDets.isNotEmpty()) {
            var bestIou = IOU_GATE
            var bestT: Track? = null
            var bestD: YoloDetector.Det? = null
            for (t in unmatchedTracks) for (d in unmatchedDets) {
                val i = iou(t.box, d.box)
                if (i > bestIou) { bestIou = i; bestT = t; bestD = d }
            }
            val t = bestT ?: return
            val d = bestD ?: return
            // Smoothed velocity from center delta (box already predicted forward).
            val ncx = d.box.centerX()
            val ncy = d.box.centerY()
            t.vx = 0.6f * (ncx - t.cx) + 0.4f * t.vx
            t.vy = 0.6f * (ncy - t.cy) + 0.4f * t.vy
            t.box = RectF(d.box)
            t.score = d.score
            t.hits++
            t.misses = 0
            unmatchedTracks.remove(t)
            unmatchedDets.remove(d)
        }
    }

    private fun iou(a: RectF, b: RectF): Float {
        val ix = maxOf(0f, minOf(a.right, b.right) - maxOf(a.left, b.left))
        val iy = maxOf(0f, minOf(a.bottom, b.bottom) - maxOf(a.top, b.top))
        val inter = ix * iy
        val union = a.width() * a.height() + b.width() * b.height() - inter
        return if (union <= 0f) 0f else inter / union
    }
}

/**
 * Counting line = a segment between two endpoints A and B (frame-normalized 0..1), so it
 * can sit at ANY angle — real doorways are rarely a perfect vertical.
 *
 * A +1 needs ALL of:
 *  - track born on the OUTWARD side (origin rule): anyone first seen inward — already
 *    boarded, the driver, a person popping up mid-frame — can never count, even if they
 *    wander back over the line and return;
 *  - center clears a DEAD BAND (~[BAND] perpendicular distance) past the line before a
 *    side "counts" as entered: a hand hovering on the line jitters inside the band and
 *    never registers a crossing;
 *  - once per track ([Track.counted]).
 *
 * [inwardSign] (+1 / -1) picks which side of A->B is "boarding". Sides here are stored
 * relative to inward: +1 = inward, -1 = outward.
 */
class LineCrossCounter(
    var ax: Float = 0.5f, var ay: Float = 0.05f,
    var bx: Float = 0.5f, var by: Float = 0.95f,
    var inwardSign: Int = 1
) {
    companion object {
        /** dead-band half-width, in frame-normalized units (~2% of the frame). */
        private const val BAND = 0.02f
    }

    /** Perpendicular distance from the line, sign flipped so + = inward side. */
    private fun inwardDist(px: Float, py: Float): Float {
        val dx = bx - ax
        val dy = by - ay
        val len = kotlin.math.hypot(dx, dy).coerceAtLeast(1e-4f)
        val cross = dx * (py - ay) - dy * (px - ax)
        return cross / len * inwardSign
    }

    /** Returns how many NEW inward crossings happened this frame. */
    fun process(tracks: List<PersonTracker.Track>): Int {
        var crossings = 0
        for (t in tracks) {
            val d = inwardDist(t.cx, t.cy)
            val zone = when {
                d > BAND -> 1   // clearly inward
                d < -BAND -> -1 // clearly outward
                else -> 0       // inside the dead band: keep previous state
            }
            if (zone == 0) continue
            if (t.prevSide == 0) {
                t.prevSide = zone
                t.originSide = zone
                continue
            }
            if (zone != t.prevSide) {
                if (zone == 1 && t.originSide == -1 && !t.counted) {
                    t.counted = true
                    crossings++
                }
                t.prevSide = zone
            }
        }
        return crossings
    }
}
