# CameraApp Plan — CameraCount Mobile (camera-based passenger counting)

> **NAMING (2026-07-04): "FleetWise" is retired — never use it for anything new.** The whole
> suite is **RouteSync**: web dashboard = `RouteSyncWeb/`, driver app = `RouteSyncMobile/`,
> camera app = `CameraCountMobile/`. Rename was folders-only: `.csproj` names and C#
> namespaces inside the two existing projects still say FleetWise* (accepted legacy, zero
> code churn). Repo root dir + GitHub remote still named FleetWise — manual rename owed.
>
> Companion app to RouteSync driver app (`RouteSyncMobile/`). A **separate native Android app** that
> auto-counts boarding passengers with the phone's camera and writes the count to the
> shared Supabase DB in near-realtime. Built to replace the manual counter used only for
> the AppDev-subject demo; manual stays as an automatic fallback.
>
> **Audience: Fable 5.** This doc is the execution spec. Every decision below is locked.
> Target client/deploy: **BGC Bus**. Capstone deadline: **SY 2026/2027**.

---

## 0. Locked decisions (the design tree)

| # | Decision | Choice |
|---|---|---|
| Q1 | CV runtime | **On-device**, native Android. Realtime DB writes (not offline-only). |
| Q2 | Linkage / trigger | Bind device → **vehicle_id**. Poll `trips` every **3–5s** for that vehicle's Active trip → auto start/stop. No app-to-app coupling. |
| Q3 | Count ownership | Camera writes existing `total_boarded`. Manual **auto-fallback** via heartbeat, hidden while camera alive. |
| Q3b | Cadence | Camera writes count+heartbeat every **5s**. RouteSync declares camera dead at **12s** stale. |
| Q4 | Counting method | **Line-cross past the pay zone**, front camera, **user-adjustable** line (in-app calibration). |
| Q5 | Model stack | **YOLO11n** (person) → **LiteRT/TFLite** (GPU/NNAPI) → **ByteTrack** → CameraX. Target **≥90%** vs manual ground truth. |
| Q6 | Language | **Kotlin** native Android. |
| Q7 | Auth (DB write) | **Anon/publishable key now** (matches RouteSync). Scoped device token deferred to the project-wide RLS lockdown. |

---

## 1. Why this works (feasibility)

On-device person counting on a phone is a **solved problem**, not research. The hard part
here is not the AI — it's framing, lighting, occlusion, and linking two phones. The BGC Bus
setup removes most of the hard part:

- **Single entry door** at front near the driver; exit is a second door with no camera →
  **count boarding only**, no in/out direction math, no net-occupancy tracking.
- Riders **pause at a QR/BEEP payment point** on entry → they funnel **one-at-a-time,
  slow, isolated** → occlusion (the usual killer) is mostly gone. Best-case counting.

`total_boarded` semantics = cumulative boarders, which is exactly what a line-cross counter
produces.

---

## 2. Architecture — the DB is the only bridge

The two phones **never talk to each other**. Both talk to the same Supabase DB. Linkage is
emergent from a shared row, not a message channel.

```
 Driver's phone                     Bus dashboard phone
 (RouteSync / MAUI)                 (FleetWiseCounter / Kotlin)
        |                                   |
        | StartTrip: trips.trip_status=Active
        |                                   | poll every 3-5s:
        |                                   |   GET active trip for my vehicle_id
        |                                   |   -> found -> lock trip_id, start camera
        v                                   v
 ┌────────────────────── Supabase (Postgres, REST) ──────────────────────┐
 │  trips: trip_status, total_boarded, count_heartbeat, actual_start...   │
 └───────────────────────────────────────────────────────────────────────┘
        ^                                   |
        | reads count_heartbeat             | every 5s: PATCH total_boarded + count_heartbeat
        | fresh -> hide manual, mirror #     |
        | stale -> reveal manual counter     v
   web dashboard reads total_boarded (live, unchanged)
```

**"When driver starts trip, camera starts"** is not a feature you build — it falls out of
the poll seeing `trip_status=Active` on the bound vehicle.

---

## 3. Existing system facts (for Fable 5)

- Supabase URL: `https://vrtluruqaxutecydbrsq.supabase.co`
- Publishable/anon key (client-safe, already embedded in RouteSync):
  `sb_publishable_sjkjW2K7QOPRKmixJdhSgA_8rPtoFzD`
- REST base: `{url}/rest/v1/` — headers on every call: `apikey: <key>` and
  `Authorization: Bearer <key>`.
- `trips` PK = `trip_id` (string). Relevant columns today:
  `trip_status` (`Active`/`Completed`/…), `total_boarded` (int), `estimated_revenue`,
  `actual_start_time`, `vehicle_id` (string, e.g. `BUS-01`), `route_id` (int).
- RouteSync start-trip: `RouteSyncMobile/Services/DriverDataService.cs:280` — PATCHes
  `trip_status=Active`, `actual_start_time=now`.
- RouteSync manual count: `RouteSyncMobile/Components/Pages/TripActive.razor` — +/- writes
  `total_boarded` every 15s + a final authoritative write on End Trip.
- **Security debt (unchanged, project-wide, parked):** `trips` (and `users`, `telemetry_data`)
  are anon-writable — anon key can PATCH anything, including `users.password_hash`. The
  camera app adds one more `trips` writer; it does **not** worsen the `users` hole. Full RLS
  lockdown is still owed before real deployment (see Phase 7).

---

## 4. DB contract changes

Only **one** new column.

```sql
-- trips: heartbeat so RouteSync knows the camera is alive
ALTER TABLE trips ADD COLUMN count_heartbeat timestamptz;
```

Web dashboard + reports keep reading `total_boarded` → **no dashboard rework**.

### Camera write (every 5s while a trip is locked)

```
PATCH {url}/rest/v1/trips?trip_id=eq.<TRIP_ID>
Headers: apikey, Authorization: Bearer, Content-Type: application/json, Prefer: return=minimal
Body:    { "total_boarded": <int>, "count_heartbeat": "<utc-now-iso>" }
```

### Camera poll (every 3–5s to find/track the trip)

```
GET {url}/rest/v1/trips?vehicle_id=eq.<VEHICLE_ID>&trip_status=eq.Active
    &select=trip_id,total_boarded,actual_start_time
```
- 1 row → lock `trip_id`, **seed local counter from `total_boarded`** (resume-safe), run camera.
- 0 rows → stop camera, stop writing.

### Count ownership rule (critical — prevents collision + reset)

- Camera counter is **monotonic**: on (re)acquiring a trip, seed `local = total_boarded`
  from DB, then only ever **increment**. Never write a value lower than what's in the DB.
- This makes camera restarts, hand-offs from manual, and manual-during-camera-downtime all
  reconcile correctly (whoever writes only pushes the number **up** to reflect new boarders).

---

## 5. RouteSync change (manual auto-fallback)

File: `RouteSyncMobile/Components/Pages/TripActive.razor`.

- On the 1s tick, read `count_heartbeat` from the trip (already reloading trip data; add the
  field to `Trip` model — `[Column("count_heartbeat")] DateTime? CountHeartbeat`).
- `cameraAlive = CountHeartbeat != null && (PhTime.Now - CountHeartbeat) < 12s`.
- `cameraAlive == true`:
  - Hide the `+ / −` buttons.
  - Show the live `total_boarded` (read-only) with a badge: **"Auto-counting (camera)"**.
  - **Stop the manual 15s flush** (camera owns the write).
- `cameraAlive == false` (stale/absent):
  - Reveal `+ / −` (existing manual behavior), resume the 15s flush.
  - On the switch to manual, seed from current DB `total_boarded` (already how it loads).

Result: hidden while camera works, **automatically reappears** within 12s if the camera
phone dies. Driver flips nothing.

---

## 6. CameraCount Mobile — Kotlin app spec

Project folder: `CameraCountMobile/` (repo root, beside `RouteSyncWeb/` + `RouteSyncMobile/`).
Package `com.routesync.cameracount`. Single-purpose: setup → poll → camera → count → PATCH.

**Stack**
- Kotlin, min SDK 26+ (CameraX + delegates).
- **CameraX** `ImageAnalysis` for frames; front camera by default.
- **LiteRT (TFLite)** runtime + GPU/NNAPI delegate.
- **YOLO11n** exported to `.tflite` (person class only), bundled as an asset.
- **ByteTrack** (Kotlin port / lightweight IOU+Kalman tracker) for persistent IDs.
- Persistence: `DataStore`/`SharedPreferences` for `vehicle_id`, bind passcode, calibrated line.
- Networking: Ktor or OkHttp for the REST poll/PATCH (no Supabase SDK needed — plain REST).

**Screens**
1. **Setup** — enter `vehicle_id`, protected by a **bind passcode** (so a random person can't
   re-point a phone at another bus). Stored locally. Editable later.
2. **Calibrate** — live camera preview + **draggable counting line/zone** overlay; save per
   device. (Placement drifts on real buses — this is required, not optional.)
3. **Run** (main) — live preview, box + line overlay, running count, trip id, "waiting for
   trip / counting" state, connection + heartbeat indicator.

**Pipeline**
```
CameraX frame -> YOLO11n (TFLite, GPU) -> person boxes
  -> ByteTrack -> stable ID per person
  -> ID crosses calibrated line INWARD (once per ID) -> local count++
  -> every 5s: PATCH total_boarded(=max(db,local)) + count_heartbeat
```
- Direction gate: only inward crossings count → filters the driver, leaners, outward motion.
- Dedup by track ID → a person is counted once even across many frames.

---

## 7. Phases (each is demoable; build in order)

### Phase 0 — DB contract + scaffold — **DONE (2026-07-04)**
- ~~`ALTER TABLE trips ADD COLUMN count_heartbeat timestamptz`~~ **DONE** (run in Supabase SQL editor).
- **Acceptance PASSED:** curl PATCH set `total_boarded=7` + `count_heartbeat` on TRIP026148,
  verified via GET, reset to 0/null. Camera write path proven end-to-end.
- ~~New Kotlin Android project~~ **DONE:** `CameraCountMobile/` scaffolded (Kotlin + Compose,
  minSdk 26, OkHttp REST client `data/SupabaseApi.kt` with `findActiveTrip`/`patchCount`/`ping`,
  in-app "Test DB connection" button). Open in Android Studio to build.
- Also done same day: folder rename `FleetWise/`→`RouteSyncWeb/`, `FleetWiseMobile/`→`RouteSyncMobile/`,
  slnx renamed + paths fixed, both projects build clean.
- **Accept:** `curl` PATCH sets `total_boarded` + `count_heartbeat`; visible in web dashboard.

### Phase 1 — Linkage skeleton (NO camera yet) — **CODE DONE (2026-07-04), device test pending**
Implemented: `data/Prefs.kt` (DataStore vehicle bind + passcode), `CounterViewModel.kt`
(4s poll → lock/release trip, 5s count+heartbeat PATCH, monotonic max(db,local) seed,
no flush after trip end — RouteSync finalize stays authoritative), `MainActivity.kt`
(Setup / Waiting / Counting screens, fake "+1" button, passcode-gated unbind dialog).
NOT yet compiled — no Android toolchain on the dev PC; build + acceptance run happens in
Android Studio on the user's side.
- Setup screen (vehicle bind + passcode).
- Poll loop 3–5s → lock Active trip for the bound vehicle.
- **Fake counter** (a manual `+1` button) → PATCH count+heartbeat every 5s. Auto start/stop
  on trip status.
- **Accept:** Start trip in RouteSync → counter app wakes, shows the trip. Press `+1` → web
  dashboard number rises live. End trip in RouteSync → counter app stops writing.
  *(This proves the entire bridge before any CV — biggest de-risk.)*

### Phase 2 — RouteSync heartbeat fallback — **CODE DONE (2026-07-05), builds clean, device test pending**
Implemented in driver app: `Trip.CountHeartbeat` mapped; `TripActive.razor` polls the trip
every 5s, `ApplyCameraState` judges freshness via `PhTime.Raw(hb) vs DateTime.UtcNow`
(camera writes TRUE UTC while RouteSync stores PH wall-clock — Raw() bridges the two
conventions), fresh -> hides +/- (pulsing "Auto-counting (camera)" badge, read-only count,
manual 15s flush suppressed), stale -> manual auto-returns. Monotonic: camera count only
raises local. Offline fail-open: 3 failed polls (~15s) -> reveal manual. Initial heartbeat
check on page load so manual doesn't flash on mid-trip restarts.
- Implement §5 in `TripActive.razor` (+ `Trip.CountHeartbeat`).
- **Accept:** Run Phase-1 fake counter → RouteSync hides `+/−`, mirrors the number read-only.
  Kill the counter app → within 12s RouteSync reveals the manual counter again.

### Phase 3 — Camera + detection — **CODE DONE (2026-07-05), model export + device test pending**
- CameraX preview + `ImageAnalysis`; YOLO11n TFLite via LiteRT (GPU delegate); person boxes
  drawn as a debug overlay.
- Implemented: `camera/YoloDetector.kt` (asset model load, GPU-or-CPU fallback, [1,84,N] /
  transposed auto-detect, conf 0.40 + NMS 0.45, person class only), `camera/DetectorAnalyzer.kt`
  (rotate → letterbox → detect → frame-normalized boxes, front-camera mirror fix),
  `ui/CameraScreen.kt` (permission flow, FIT_CENTER preview + box overlay, fps/ms/GPU stats,
  graceful "model missing" screen). "Camera" button in header opens it.
- **USER STEP:** export model once — `pip install ultralytics` then
  `yolo export model=yolo11n.pt format=tflite imgsz=320` → copy
  `yolo11n_saved_model/yolo11n_float32.tflite` into `CameraCountMobile/app/src/main/assets/`
  (see assets/README.txt).
- **ACCEPT PASSED (2026-07-06):** real phone (Xiaomi), front cam, boxes hug people, 2 persons
  on photos, ~8-9 fps @ 113ms CPU. GPU delegate unsupported on device -> auto CPU fallback
  (expected, still usable). Model = user-downloaded `yolo11n.tflite` (float32, 10.6MB) —
  turned out **NCHW `[1,3,320,320]`** (ONNX-style), so YoloDetector auto-detects NHWC vs NCHW
  input layout + fills the buffer accordingly. Crash-on-reopen fixed (teardown order:
  unbind camera -> shutdown executor -> close interpreter; detect()/close() synchronized;
  per-frame try/catch surfaces errors in the UI chip). ImageAnalysis forced to RGBA_8888
  (YUV toBitmap() threw on device). Hand/partial-body sometimes reads as a person — normal
  for COCO nano; Phase 4 line-cross + tracker filters stray boxes.

### Phase 4 — Tracking + line-cross counting — **CODE DONE (2026-07-06), device test pending**
- ByteTrack IDs; hardcoded line first; count once per ID crossing inward.
- Replace the Phase-1 fake `+1` with the **real** count feeding the same PATCH path.
- Implemented: `camera/PersonTracker.kt` — ByteTrack-lite (two-stage IoU association:
  high-conf ≥0.40 matches first, low-conf 0.25-0.40 rescues occluded tracks; only high-conf
  births tracks; MIN_HITS=3 confirms — kills one-frame ghosts like hands; MAX_MISSES=12
  ~1.2s; constant-velocity prediction, smoothed) + `LineCrossCounter` (vertical line
  x=0.5 normalized, inward = left→right in mirrored preview, once per track via
  `counted` flag; tracks born on the inward side — driver/seated — can never count).
  Detector CONF_THRESHOLD dropped 0.40→0.25 to feed the two-stage tracker.
- Wiring: trip Active → Root swaps to full-screen `CameraScreen(vm)` — camera runs ONLY
  during trips (auto start/stop + heat control); crossings call `vm.increment()` → same
  5s count+heartbeat PATCH from Phase 1. Fake +1 deleted. Counted tracks draw gray,
  live tracks teal, dashed amber line, bottom HUD = count + trip + sync state. Preview
  mode (Waiting header "Camera") unchanged for aiming.
- NMS IoU tuned 0.45→0.55 (device test): two people standing close were merged into one box
  at 0.45 → undercount; 0.55 keeps them as two while still merging same-person duplicates.
- **Bench test PASSED (2026-07-06):** single + two-person crossings count correctly,
  direction-gated, counted boxes gray out. Line still mid-frame placeholder → Phase 5.

### Phase 5 — Calibration UI — **CODE DONE (2026-07-06), device test pending**
- Live preview + **draggable line/zone**, saved per device; counting uses the saved line.
- Implemented: Waiting-header "Calibrate" → CameraScreen(calibrate). Line is now a
  **two-endpoint segment (any angle / diagonal)**, not a fixed vertical — LineCrossCounter
  uses side-of-line via 2D cross product. Two draggable grab dots (nearest-handle picking),
  "Flip boarding side" (perpendicular inward arrow), "Save line" → DataStore
  (`line_ax/ay/bx/by` + `line_inward_sign`, Prefs.LineCalibration). Counting loads the saved
  line on entry (crossings gated until loaded).
- NOTE: calibration only reachable from the Waiting state (no active trip). During a trip
  the app is the locked full-screen counter. User's earlier "can't drag / no buttons" was
  being on the counting screen mid-trip, not the calibrate screen.
- **Accept:** Drag dots (diagonal ok), save, restart app → line persists and is used.

### Phase 6 — Field hardening — **CODE DONE (2026-07-06/07), accuracy run pending**
- ~~Offline durability~~ **DONE:** count persisted to DataStore per crossing (survives
  kill/reboot); flush retries; **post-trip reconcile** = raise-only PATCH
  (`total_boarded=lt.N` + device guard) so a dead-zone count that beat the driver's manual
  lands even after the trip closed (retries every poll pass + next app start).
- ~~Resume-from-DB~~ **DONE:** re-acquire seeds `max(local, db, savedPending)`; pending from
  an older trip reconciles separately.
- ~~Keep-screen-on + foreground service~~ **DONE:** `CountingService` (dataSync FGS) runs
  per-trip; screen never sleeps on camera screens.
- ~~Camera stall guard~~ **DONE:** no frames 12s → heartbeat PATCHes stop → driver manual
  reappears; HUD "camera stalled". ~~Thermal~~ **DONE:** SEVERE→infer every 2nd frame,
  CRITICAL→3rd (listener-driven).
- **Also landed (beyond plan):** one-phone-per-bus locks (bind claims
  `vehicles.counter_device_id`; trip claim + 30s-stale takeover on `trips.counter_device_id`
  — DDL for BOTH columns applied); false-+1 fix (origin rule: only tracks born outward may
  count + 2% dead band kills line-jitter); front/back camera toggle w/ widest-lens pick +
  FOV readout; input validation + fleet dropdown bind; dim mode; charging banner;
  count-flash; trip summary.
- **Accept (code paths):** dead-zone sim buffers then catches up ✅ (tested 2026-07-06,
  incl. trip-ended-while-offline reconcile); reboot/kill resume — pending device test;
  **accuracy run ≥90% — PENDING (last Phase-6 box).**

### Phase 7 — Security hardening — **PLANNED (grilled 2026-07-07), see `PHASE7-security-plan.md`**
> Full project-wide plan lives in repo-root `PHASE7-security-plan.md` (local, untracked).
> Locked: edge-fn-minted custom JWTs (keep users table), fleet bind secret for camera
> tokens, long tokens + DB-join revocation, anon = zero, users_app view hides hashes,
> live-DB cutover 7a→7d with per-table rollback. Section below kept for history.
- Do the parked RLS lockdown: `trips`/`users`/`telemetry_data` no longer anon-writable; anon
  read-only.
- Issue each camera device a **scoped token** that can PATCH only `total_boarded` +
  `count_heartbeat` on **its bound vehicle's Active trip**.
- **Accept:** Anon key can no longer PATCH `trips`; camera token can PATCH only its vehicle's
  active trip. `users.password_hash` no longer anon-writable.

---

## 8. Known edges / risks (log, don't over-build now)

- **Dead zone looks like death.** Camera offline → heartbeat stops reaching DB → RouteSync
  *could* wrongly show manual if the driver's phone is somehow still online (different
  SIM/carrier). Narrow (both phones usually share bus coverage). The monotonic
  `max(db, local)` write rule (§4) makes the reconcile safe on reconnect — no double count,
  numbers only go up. Full fix = camera reclaims authority on fresh heartbeat.
- **Dashboard heat** (tropical, closed bus, sun) → thermal throttle/shutdown = the #1 real
  failure. Mitigate: lower preview res, cap inference fps, ventilation/mount guidance; the
  heartbeat fallback covers the driver when it does die.
- **Mount slip** → bad framing → silently wrong counts (worse than a clean death). Mitigate:
  calibration screen + a "counting looks off?" recalibrate prompt.
- **Two shoulder-to-shoulder crossings** → the pay-point pause naturally serializes riders,
  so this stays rare. Accept for now.
- **Alighting not counted** — by design (exit door has no camera). `total_boarded` is
  cumulative boardings, matching current semantics.

---

## 9. Accuracy target + test method

- Target: **≥90%** count accuracy vs a human tally over a real boarding run. (Lab ~95%+; real
  bus with glare/motion typically 85–92% — the pay-point pause keeps you at the high end.)
- Method: during a test trip, a person tallies boardings manually; compare to the camera's
  `total_boarded` at end. Log misses/double-counts to tune the line placement + confidence
  threshold. This comparison is also a strong **defense demo** ("camera 47 vs manual 48").

---

## 10. Phase 9 — power/heat + resilience (CODE DONE 2026-07-13; device test pending)

Two small camera-app improvements. Neither touches the DB or the other apps. Build order
below; both are low-risk (no new permissions, no OEM background-camera exposure).

### 9a — Resting throttle + instant un-dim (the real heat lever)

**Problem:** dim mode today (`CameraScreen.kt`: 60s no-detection -> near-black overlay,
re-checked every 5s) only saves OLED power. YOLO `detector.detect()` STILL runs full rate
under the black, so the dominant heat source (inference) is unchanged. And un-dim lags up
to 5s (waits for the next poll tick).

**Fix — motion-gate inference while resting:**
- `DetectorAnalyzer` already frame-skips via `throttle()` (`if (frameNo++ % n == 0L)`),
  currently thermal-driven (1 normal, 2-3 hot). Add a "resting" input: while `dimmed`,
  raise N to ~4 -> 1-of-4 frames inferred (~7 checks/sec on 30fps). Cuts inference heat
  ~75% while the doorway is empty. Full rate (N=1, or the thermal value) the moment
  activity resumes.
- **Instant un-dim:** flip `dimmed=false` inside the analyzer result callback the moment
  `dets` is non-empty, not on the 5s loop. Kills the up-to-5s wake lag.
- Safety: 1-of-4 still samples a boarder ~7x/sec; a person is in frame ~1-2s, so caught
  within ~130ms -> un-dim -> full rate. ByteTrack velocity-predict + MIN_HITS bridge the
  few skipped frames. Net: no accuracy loss, big idle-heat cut.
- Wire `dimmed` (state in `DetectionSurface`) into the `throttle` lambda alongside
  `thermalSkip` -> `throttle = { maxOf(thermalSkip, if (dimmed) 4 else 1) }`.

### 9b — Kiosk auto-launch on shift start (resilience)

**Already partly works:** camera polls `trips` every 4s; trip -> Active auto-switches
Waiting -> Counting IF the app is foreground (it is by design — mounted fixture,
`keepScreenOn`). Gap = app killed / backgrounded / post-reboot.

**Fix — keep it the foreground app, always:**
- **Lock-task / kiosk mode** (primary): pin the app so it can't be swiped away or
  backgrounded; trip-start transition then always fires. Cleanest for a dedicated
  dashboard phone. (`startLockTask()`; device-owner or screen-pinning.)
- **BOOT_COMPLETED receiver:** relaunch the app after a reboot (power blip on the bus).
- Optional **full-screen-intent** notification on trip-start as a backstop to pull the UI
  forward if it ever slips to background (the alarm/call mechanism; `USE_FULL_SCREEN_INTENT`,
  Android 14 gate). Only if kiosk proves insufficient.
- Verdict: kiosk + boot receiver covers the real deployment (fixed, plugged fixture); the
  full-screen-intent is a nice-to-have, not required.
- **BUILT + EXTENDED (2026-07-13): WatcherService** — always-on specialUse FGS (the one
  type startable from BOOT_COMPLETED on API 34+). Polls findActiveTrip every 15s while
  the UI is NOT visible (MainActivity.uiVisible flag); trip Active -> direct
  startActivity when "Display over other apps" granted (one-time Waiting-screen banner
  asks for it), else full-screen-intent notification. Started from boot AND every app
  open. Result: driver taps Start Trip -> camera app opens itself even if closed/
  backgrounded. Only unavoidable touch = first open after INSTALL (Android stopped-state
  rule, and binding needs it anyway) + the one-time overlay grant. OEM autostart
  settings (Xiaomi/Huawei) may need enabling once.

### Deferred (NOT building): screen-off headless counting

Possible (foreground service + wake lock + headless CameraX analysis, reusing
`SnapshotCapture.OneShotLifecycle`), but the thermal win is modest — inference is the hot
part and runs screen-on or off, so screen-off only saves display (~15-30%). Adds
`FOREGROUND_SERVICE_CAMERA` (Android 14) + OEM background-camera-kill risk, and loses the
local count/preview. 9a delivers most of the heat benefit at a fraction of the risk. Revisit
only if 9a + field thermal data show inference alone still overheats.
