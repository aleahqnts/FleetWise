# Phase 8 — Remote Camera Control (calibrate the untouchable phone)

> Status: PLANNED (grilled + locked 2026-07-07). Fable-5-ready. Depends on Phase 7 auth
> (see `PHASE7-security-plan.md`) for write scoping. Local planning doc, NOT committed.

## 0. Problem

The camera phone may be mounted high / unreachable. Today calibration (line placement,
flip, lens) is only doable ON the phone (Compose canvas, DataStore-local, reachable only
from Waiting). Goal: control + calibrate the camera **remotely** from the driver app
(driver, in-the-moment) and web dashboard (admin, setup). Not fantasy — the whole system
is already DB-as-bridge; this extends the same pull loop.

## 1. Locked decisions (grilled 2026-07-07 — do not relitigate)

| # | Decision | Choice |
|---|----------|--------|
| 1 | Scope / phasing | **Two tiers, phased (C).** Tier 1 = blind commands (flip side, nudge line, swap lens, recenter, reset, re-bind) — no camera view needed. Tier 2 = true remote calibration via snapshot. Tier 1 first; Tier 2 rides the same channel. |
| 2 | Source of truth | **`device_config` DB row is authoritative (A).** Camera becomes a FOLLOWER: reads config every poll, applies; local on-phone calibration writes UP to the same row. DataStore = offline cache only. Last-write-wins via `version`/`updated_at`. |
| 3 | Control surface | **Both (C), driver-first.** Driver app controls its ACTIVE-TRIP vehicle's camera (JWT-scoped, Phase 7 join). Web/admin controls any camera anytime (service key). Same `device_config` row → web is a 2nd UI on identical data. |
| 4 | When applied | **Live, mid-trip (A).** Camera picks up config next poll (~4s) and swaps immediately, reusing `PersonTracker.resetCrossingState()` (already built) so no double-count / miss across the change. No deferral — the point is in-the-moment fixes. |
| 5 | Snapshot window | **Maintenance-wake anytime (B).** New camera lifecycle: sensor-on WITHOUT counting. A `wake` request in config → always-polling app opens sensor briefly, captures, uploads, holds preview until save/timeout. Works on a parked bus (empty doorway, install-time). Requires app process alive (true for a left-on fixture). **Biggest new surface in this feature.** |
| 6 | Snapshot transport | **Supabase Storage bucket (A).** `snapshots/{device_id}.jpg`, camera uploads (OkHttp multipart), driver/web downloads (signed URL). Blob NEVER on the hot-polled `device_config` row. |
| 7 | Snapshot freshness | **Frozen still + on-demand refresh (A).** One clean still on wake (empty doorway = best reference). "Refresh" button → one new capture on demand (verify a body on the line). No continuous upload. |
| 8 | Acknowledgment | **Separate `device_status` row, camera-written (A).** Config = desired (driver-written); status = reported (camera-written) → no same-row write races. Fields: `last_seen`, `wake_state`, `snapshot_ready_at`, `applied_at`, `config_version_applied`. Driver shows ✓ only when `config_version_applied == device_config.version`. |
| 9 | Snapshot privacy | **Ephemeral, aggressively deleted (A).** One transient image per open session; deleted on apply / cancel; hard TTL purge (~10 min). No history of bus interiors. Wake scoped to Q3 controllers. |

## 2. Coordinate contract (the correctness key)

Line coords are frame-normalized (0..1) relative to the UPRIGHT camera frame — this is
exactly what `DetectorAnalyzer` emits and `LineCrossCounter` consumes today. The snapshot
IS that frame. So the driver-app editor: display the snapshot letterboxed, let the user
drag two handles relative to the DISPLAYED image rect, normalize to 0..1 of that rect →
the coords transfer 1:1 to where the camera applies the line. **No aspect/rotation math
needs to agree beyond "normalize against the same frame."** Get this wrong → line lands
somewhere else than the driver placed it.

## 3. Schema (new)

```sql
-- desired state (driver/admin/web + camera-local calibrate all write here)
create table device_config (
  device_id   text primary key,          -- = vehicles.counter_device_id
  line_ax real, line_ay real, line_bx real, line_by real,
  inward_sign int  default 1,
  use_back_camera boolean default false,
  wake_requested_at timestamptz,          -- Tier 2: driver asks camera to wake+snap
  version     int  not null default 0,    -- bumped on every write; camera echoes it back
  updated_by  text,                        -- 'driver:7' | 'admin' | 'device'
  updated_at  timestamptz default now()
);

-- reported state (camera writes ONLY its own row)
create table device_status (
  device_id   text primary key,
  last_seen   timestamptz,                 -- liveness (like count_heartbeat)
  wake_state  text default 'idle',         -- idle|capturing|preview|applied
  snapshot_ready_at timestamptz,
  applied_at  timestamptz,
  config_version_applied int default -1
);
```
Storage bucket `camera-snapshots` (private), object `{device_id}.jpg`, object TTL ~10 min.

## 4. Camera app (Kotlin) changes

- **Config follower:** poll loop also GETs `device_config` for `device_id`; if
  `version > lastAppliedVersion`, apply (line/lens), call `resetCrossingState()`, write
  `device_status.config_version_applied = version`. DataStore keeps the last applied config
  as offline cache (seed on launch before first DB read).
- **Local calibrate writes UP:** on-phone Save also PATCHes `device_config` (version+1,
  updated_by='device') so the DB stays the truth.
- **Heartbeat status:** write `device_status.last_seen` on the existing flush cadence
  (extends the heartbeat already sent).
- **Wake lifecycle (NEW, biggest surface):** poll sees `wake_requested_at` newer than last
  handled → bring the sensor up outside counting (reuse `CameraScreen` capture path in a
  headless/preview mode), set `wake_state=capturing`, grab one frame, JPEG-encode, upload
  to bucket, set `snapshot_ready_at` + `wake_state=preview`. Timeout (~2 min no apply) →
  release sensor, delete snapshot, `wake_state=idle`. On applied config → delete snapshot.
- **SupabaseApi:** add `getDeviceConfig`, `patchDeviceConfig`, `patchDeviceStatus`,
  `uploadSnapshot` (Storage multipart), `deleteSnapshot`.

## 5. Driver app (Blazor) changes

- **Camera control panel** on `TripActive` (active-trip vehicle only): live
  `device_status` chip (online/offline via `last_seen`), Tier-1 buttons (Flip side, Nudge
  ◀▲▼▶, Swap lens, Recenter, Reset) → each PATCHes `device_config` (version+1). Shows ✓
  when `config_version_applied` catches up.
- **Tier 2 calibration screen:** "Calibrate camera" → PATCH `wake_requested_at` → poll
  `device_status` until `snapshot_ready_at` → download snapshot (signed URL) → SVG overlay
  with two draggable handles (§2 contract) → Save PATCHes line coords (version+1) → ✓ on
  echo. "Refresh" re-triggers wake capture. Cancel → camera times out + purges.
- New models `DeviceConfig`, `DeviceStatus`; Storage download helper.

## 6. Web dashboard (Blazor Server) changes

- Fleet view → per-vehicle "Camera" panel: same control set + calibration, but any vehicle
  anytime (service key, no trip needed). Identical `device_config`/`device_status`/snapshot
  plumbing — a 2nd UI on the same data. Admin/install-time surface.

## 7. Phase 7 additions (fold into `PHASE7-security-plan.md` policy matrix)

- `device_config`: **app_driver** UPDATE(line_*, inward_sign, use_back_camera,
  wake_requested_at, version, updated_by, updated_at) WHERE `device_id` = the driver's
  ACTIVE-TRIP vehicle's `counter_device_id`; **app_camera** UPDATE own row; SELECT both.
- `device_status`: **app_camera** UPSERT own row only; **app_driver** SELECT its
  active-trip vehicle's row.
- bucket `camera-snapshots`: **app_camera** write `{own device_id}.jpg`; **app_driver**
  read its active-trip vehicle's object; admin/web via service key. Object TTL purge.
- anon: none (unchanged).

## 8. Build order

1. **8a — Schema + camera follower (Tier 1 core).** Tables, camera reads config + applies
   + echoes version + writes last_seen. Prove: edit `device_config` in SQL editor → camera
   swaps line live. No UI yet.
   **STATUS: CODE DONE (2026-07-10).** `supabase/phase8a.sql` (tables RLS-on at creation,
   anon/authenticated revoked, `driver_active_camera()` helper, camera own-row ALL policies,
   driver active-trip SELECT + column-scoped UPDATE). Camera: `Prefs.configVersion/
   bumpConfigVersion/applyRemoteConfig` (atomic), `SupabaseApi.getDeviceConfig/
   upsertDeviceConfig/upsertDeviceStatus`, `CounterViewModel.followDeviceConfig()` per 4s
   poll (DB behind local -> push up self-heal; DB newer -> apply + echo; in-sync -> last_seen
   heartbeat every 3rd tick ~12s). CameraScreen collects line/lens flows (live apply, paused
   while dragging), `resetCrossingState()` on ANY line change, local Save + lens toggle bump
   version + write UP. APK builds. PENDING: run phase8a.sql, device test (SQL-edit -> line
   swaps live).
2. **8b — Driver Tier-1 panel.** Buttons on TripActive, status chip, ✓ echo.
   **STATUS: BUILT then SUPERSEDED by user decision (2026-07-11).** Blind nudge/flip/
   lens/reset buttons removed — photo calibration (8d) is the ONLY driver surface.
   TripActive keeps a slim "Camera" card: Online/Offline chip (device_status.last_seen,
   30s stale) + "Calibrate Camera" button. Lens swap moved INTO the 8d editor ("Flip
   Camera": PATCH use_back_camera + version -> wait echo -> auto re-wake for a photo
   from the new lens). Tier-1 RLS/grants stay (8d + web use the same write path).
3. **8c — Wake + snapshot pipeline (Tier 2 camera side).** Wake lifecycle, capture, bucket
   upload, status states, TTL/purge.
   **STATUS: CODE DONE (2026-07-11).** `supabase/phase8c.sql` (private bucket
   camera-snapshots 2MB/jpeg-only; storage.objects policies: camera ALL on own
   {device_id}.jpg, driver SELECT on active-trip object). Camera: `SnapshotCapture`
   (headless one-shot: throwaway LifecycleOwner, RGBA analysis, 5-frame AE warm-up,
   upright + FRONT-MIRRORED to display space, same ultrawide min-zoom as counting so
   FOV matches), `DetectorAnalyzer.frameTap` (counting-mode grab off the live session —
   no second camera bind), `CounterViewModel.handleWake()` per poll (fresh <3min +
   newer-than-last-handled guard -> capturing -> upload (1280px JPEG q80) -> preview +
   snapshot_ready_at; 2-min timeout purge -> idle; purge on config apply too),
   `SupabaseApi.uploadSnapshot/deleteSnapshot` (x-upsert binary PUT). KEY GOTCHA:
   snapshot must be in DISPLAY space (mirrored for front lens) — line coords live there.
   APK builds. PENDING: run phase8c.sql, wake test via SQL (set wake_requested_at=now()),
   verify storage object + wake_state transitions + 2-min purge.
   VERSION-COLLISION FIX (2026-07-11): both write paths re-read DB version right before
   writing and use max(db,local)+1 (driver NextVersionAsync; camera nextConfigVersion).
   Was: two editors branching +1 off the same base wrote the SAME version, different
   content -> follower's `>` skips the equal write -> silent desync (repro: camera
   on-phone save + driver save at same base). Not airtight vs true ms-concurrent saves
   (that needs a DB trigger owning version, fix #2 — deferred, humans can't hit the
   window). On-phone calibrate ~never happens in deployment anyway (phone unreachable).

4. **8d — Driver Tier-2 calibration screen.** Snapshot download + SVG line editor + save.
   **STATUS: CODE DONE (2026-07-11).** `CameraCalibrate.razor(.css)` at
   /camera-calibrate/{TripId} (entry: "Calibrate on a photo" button in TripActive cam
   panel). Flow: PATCH wake_requested_at -> poll device_status (20x1.5s) for
   snapshot_ready_at >= wakeSentAt-5s slack -> authenticated Storage GET
   (/object/authenticated/, driver JWT, RLS-scoped) -> data-URL img + SVG overlay.
   Drag loop lives in wwwroot/js/calib.js (pure JS like ptr.js — WebView bridge too
   slow per-move; pixel-space viewBox so handles stay round; ResizeObserver redraw;
   .NET gets coords on drag END via OnLineChanged). Save PATCHes line doubles +
   inward_sign + version+1 + updated_by driver:<uid> -> polls echo -> "✓ Applied";
   echo timeout -> "Saved — applies on reconnect". "New photo" re-wakes keeping the
   dragged line; Flip boarding side included (arrow drawn like CameraScreen). Driver
   RLS = ACTIVE trip only by design; parked-bus calibration = web/admin (8e).
   Compile validated. PENDING: two-phone device test.
5. **8e — Web/admin panel.** Same control on the dashboard, any vehicle.
   **STATUS: CODE DONE (2026-07-12).** Vehicles tab -> View Details -> "Passenger
   Counter" section -> Calibrate Camera overlay (custom overlay above the Bootstrap
   modal). Same photo editor as 8d, adapted: wwwroot/js/camera-calib.js (drag +
   wake/poll/save flows), _CameraCalibrateModal.cshtml (markup + scoped styles).
   Service key NEVER reaches the browser — VehiclesController proxy endpoints:
   CameraState (device+config+status JSON), CameraWake (PATCH wake_requested_at),
   CameraSnapshot (server-side storage GET -> File(), no-store), CameraSave
   (re-reads version server-side then +1, updated_by='admin'; doubles passed as
   invariant-culture strings to dodge MVC culture binding). Web Vehicle model +
   VehicleDetailsViewModel gained CounterDeviceId. Works on a parked bus — no trip
   needed, which the driver surface deliberately can't do. Compile validated.
   PENDING: browser test (web run + camera phone on Waiting).
6. Phase 7 policies applied to the new surfaces at their cutover.

## 8.5 Scaling item (Phase 8+, NOT for pilot) — polling → Realtime

Whole suite is DB-as-bridge via **fixed-interval polling**: driver app runs 4 loops
(Home/Trips/Notifications/MessageWatch) at 5s; camera polls ~4s; Phase 8 adds a
`device_config` GET to the camera loop. Cost is **O(devices × frequency), sustained** —
every tick is a real PostgREST req + Postgres query + (post-Phase-7) an RLS policy eval
(`driver_is_active()` / `camera_vehicle()`, with joins), whether or not any row changed.

- **1-bus pilot (BGC):** ~1 req/s total. Trivial. 5s intervals are correct here — the
  "near-live" feel is worth it, load is a non-issue.
- **Real multi-bus fleet:** load multiplies by device count and runs 24/7 even when idle;
  ~90%+ of polls return identical bytes (pure waste — DB CPU, bandwidth, phone battery).
  Dropping 15s→5s tripled per-device load, which is fine at N=1 but multiplies against
  every added device.

**Fix at fleet scale (do NOT do now):** migrate the poll loops to **Supabase Realtime
(websocket subscriptions)** so the DB pushes only on actual row changes → load scales with
*changes*, not device-count, and updates arrive instantly instead of up-to-5s-late. Applies
to all four driver loops AND the Phase 8 camera `device_config`/`device_status` loop — the
config-follower is a natural first Realtime candidate (a bus's config changes rarely, so a
subscription is near-zero cost vs. a 4s poll). Trigger to revisit: device count climbs past
a handful, or Supabase egress/compute shows sustained idle load.

## 9. Accept

- SQL-edit config → camera applies within one poll, `resetCrossingState` runs, count sane.
- Driver flip/nudge/lens from TripActive → camera obeys, ✓ shows on echo.
- Driver "Calibrate" on a parked bus → camera wakes, still appears, drag+save → camera
  adopts the exact line (verify a crossing lands on the placed line). Snapshot gone after.
- Offline camera → driver sees "not responding" (stale `last_seen`); on reconnect it
  catches up to the latest `version`.
- Phase 7: driver JWT cannot touch a non-active-trip vehicle's config; anon nothing.
