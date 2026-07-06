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
        /** which side of the counting line this track was on last frame; 0 = unseen */
        var prevSide = 0
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
 * Vertical counting line in frame-normalized coords. A confirmed track whose center
 * crosses from the outward side to the inward side is counted ONCE (track.counted).
 *
 * Defaults are Phase-4 placeholders (line mid-frame, boarding = left -> right in the
 * mirrored preview). Phase 5 replaces them with on-screen draggable calibration.
 * Tracks first seen already on the inward side (driver, seated passengers) have no
 * outward history -> can never count.
 */
class LineCrossCounter(
    var lineX: Float = 0.5f,
    var inwardPositive: Boolean = true
) {
    /** Returns how many NEW inward crossings happened this frame. */
    fun process(tracks: List<PersonTracker.Track>): Int {
        var crossings = 0
        for (t in tracks) {
            val side = if (t.cx >= lineX) 1 else -1
            if (t.prevSide == 0) { t.prevSide = side; continue }
            if (side != t.prevSide && !t.counted) {
                val inward = if (inwardPositive) side == 1 else side == -1
                if (inward) { t.counted = true; crossings++ }
            }
            t.prevSide = side
        }
        return crossings
    }
}
