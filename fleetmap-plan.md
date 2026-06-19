# Fleet Map — Fix Plan

Status: planning. Branch `feature/reports`, synced to `origin/main` `17c9cb0` (incl. colleagues' map fix PR #27).

---

## 1. How it actually works (pipeline)

```
DRIVER PHONE                          SUPABASE                   WEB MAP
LocationTrackingService  --POST-->    telemetry_data   <--read-- FleetMap/Positions
(GPS every ~5s, buffered    raw REST  (lat,lng,pax,      latest   -> fleetmap.js
 in local SQLite, flushed)            speed,heading)     per trip  (Leaflet markers)
```

- Map = **pure reader** of `telemetry_data`. Bus pin = latest telemetry row of an **Active** trip. No GPS row -> no pin.
- Real driver path: `StartTripAsync` -> trip `Active` + `actual_start_time` + vehicle `On Trip` -> `LocationTrackingService` streams GPS -> `telemetry_data` -> map.
- Write path key files:
  - `FleetWiseMobile/Platforms/Android/LocationTrackingService.cs` — GPS poll, write rule (moved >=25m OR pax changed OR 60s heartbeat).
  - `FleetWiseMobile/Services/TelemetryQueue.cs` — local SQLite buffer -> REST POST to `telemetry_data`; also flushes trip finalizes.
  - `FleetWiseMobile/Services/DriverDataService.cs` — `StartTripAsync` / finalize.
- Read path: `FleetWise/Controllers/FleetMapController.cs` `Positions()` + `FleetWise/wwwroot/js/fleetmap.js`.

**No geofence anywhere in the write path** — telemetry lat/lng = whatever the phone sends. Tracking works ANY location (BGC or not).

---

## 2. Colleagues' fix already merged (PR #27 `b24537f`)

Touches `FleetMapController.cs`, `Views/FleetMap/Index.cshtml`, `fleetmap.js`. Summary:

- **Removed the moving-bus gate**: previously `if (DisplayStatus(vehicle_status) != "Active") continue;`. Now **any Active trip with telemetry is shown moving**, regardless of `vehicle_status`.
- Added `maintenance_logs` read -> `flaggedVehicleIds` (open incidents). Parked buses now colored by **Flagged** / **Out of Service** (aligns with our flag model: out-of-service wins, then flag, then `NormalizeParked`).
- Moving-bus status label `"Active"` -> `"On Trip"`; `DisplayStatus` replaced by `NormalizeParked` (stale moving/Flagged/Ready -> "Ready to Deploy").
- View: status filter `"Active (On Trip)"` -> `"On Trip"`, added `"Out of Service"`.
- JS: `statusColor` updated (slate for Out of Service), parked filter `'Active'` -> `'On Trip'`.

**Impact on this plan:**
- They did NOT touch `TelemetrySimulator` -> rogue simulation still present. **P0 (kill simulator) still required.**
- Gate removal makes sim trips show moving *more* easily (no `vehicle_status` requirement). Cosmetically worse until P0/P1.
- New baseline for real testing is *easier*: a bus moves on just `Active` trip + telemetry. `StartTripAsync` sets `On Trip` anyway, so real path unaffected, just less brittle.
- Cleanup discriminator unchanged (see P1).

---

## 3. Root cause — rogue trips (TRIP026053 / 026054)

`FleetWise/Services/TelemetrySimulator.cs` — a `BackgroundService` (colleagues'). Every 5s it:

1. `EnsureActiveTripsForOnTripVehiclesAsync()` -> any vehicle `vehicle_status="On Trip"` + route but no Active trip -> **auto-creates an Active trip**. ← rogue trip generator.
2. Animates every Active trip with `actual_start_time IS NULL` along route geometry -> inserts fake telemetry + **bumps `total_boarded`** -> the absurd 329 / 294 boarded.

Guard: skips trips with `actual_start_time` set (real driver trips safe from overwrite). Registered **Development-only** (`Program.cs:43`) -> fires on every local dev run.

**Clean discriminator:** sim trip = `trip_status='Active' AND actual_start_time IS NULL`. Real trip = `actual_start_time` set.

---

## 4. Phased plan

### P0 — Make the simulator controllable + self-cleaning  ✅ IMPLEMENTED (not yet deployed)
Decision (user): simulator is useful for demos -> keep, but it must be controllable and leave no residue.

**Done in code:** `SimulatorControl` singleton (`Enabled` default **false**); `TelemetrySimulator` gated on it, tags created trips `IsSimulated=true`, animates only `IsSimulated` trips; `SimulatorController` (`Start` / `Stop` / `Status`, `[Authorize]` + antiforgery); hidden `SIM` toggle bottom-right of Fleet Map (faint, green when ON; OFF confirms then deletes sim data). `Program.cs` registers both (no longer dev-only; safe because default OFF). `Trip.IsSimulated` mapped to `trips.is_simulated`.

**ROLLOUT ORDER (critical):**
1. Run the DDL in Supabase FIRST: `ALTER TABLE trips ADD COLUMN is_simulated boolean NOT NULL DEFAULT false;` — Trip writes (Add Trip / Schedule save / etc.) will 400 against a missing column, so this must land before the new build runs.
2. Rebuild + run web. Simulator now OFF -> no new rogue data; existing rogue trips freeze (no longer animated -> teleport stops).
3. Clear the existing garbage once (either path, both safe now that the producer is idle): run the §4 P1 SQL, OR flick SIM on->off to auto-clean (cleanup also catches legacy Active/no-start trips).

Design notes below.

- **Runtime toggle**, not just a config flag: hidden admin button -> controller endpoint -> start/stop the loop (`IHostedService` kept alive, internal `_running` gate; or start/stop a `CancellationTokenSource`).
- **Tag sim data.** DDL: `ALTER TABLE trips ADD COLUMN is_simulated boolean NOT NULL DEFAULT false;`. Simulator sets `is_simulated=true` on every trip it auto-creates.
- **Animate own trips only.** Change the tick filter from "any Active trip with `actual_start_time IS NULL`" to "trips where `is_simulated=true`". Sim never touches anything it didn't make.
- **Toggle OFF -> auto-cleanup.** Delete sim telemetry then sim trips by the tag:
  - `DELETE FROM telemetry_data WHERE trip_id IN (SELECT trip_id FROM trips WHERE is_simulated);`
  - `DELETE FROM trips WHERE is_simulated;`
  Real (phone) data untouched by construction — it's never tagged.
- `EnsureActiveTripsForOnTripVehiclesAsync()` stays but every trip it creates is tagged `is_simulated=true`.
- Net: ON = demo buses move; OFF = its rows vanish, real data survives.

### P1 — Clean the EXISTING garbage (one-time, DESTRUCTIVE — see §5)
Runs once for the data already in the DB (made before the `is_simulated` tag exists), so it uses the heuristic discriminator, not the tag.

- Back up `trips` + `telemetry_data` first.
- **Sim-trip signature = `trip_status='Active' AND actual_start_time IS NULL`.**
  - A real Active trip always has `actual_start_time` (set by `StartTripAsync`).
  - **DO NOT** use "no start AND no end time" — real *Not Yet Started* scheduled trips (TRIP026050/051/052…) also have neither, and would be wrongly deleted.
- **Telemetry is MIXED** (user has real phone rows here). Do NOT wipe wholesale. Delete only telemetry whose `trip_id` belongs to a sim trip; phone telemetry sits on real trips (`actual_start_time` set) -> preserved.

```sql
-- 1. REVIEW — must list only rogue trips, no real ones
SELECT trip_id, trip_status, actual_start_time, actual_end_time
FROM trips WHERE trip_status='Active' AND actual_start_time IS NULL;

-- 2. delete their telemetry (keeps phone telemetry on real trips)
DELETE FROM telemetry_data
WHERE trip_id IN (SELECT trip_id FROM trips WHERE trip_status='Active' AND actual_start_time IS NULL);

-- 3. delete the sim trips
DELETE FROM trips WHERE trip_status='Active' AND actual_start_time IS NULL;
```
- Then reset affected vehicles' `vehicle_status` to a parked state if any stuck "On Trip".
- Note: this trims the dashboard "Passengers Boarded by Hour" only for the deleted sim rows; real phone history stays.

### P2 — Prove the read path (table -> map, no phone)  ✅ DONE
- Validated live during the incident: sim writes telemetry every tick, map renders it. Also caught + fixed a UTC bug (below).

### P3 — Real phone test
- **Viewport fix ✅ DONE** — "Fit to buses" control (top-left) recenters on live moving buses; far-from-BGC pin now reachable (`fleetmap.js fitToBuses`).
- **Phone test (manual, pending):**
  1. Rebuild + deploy RouteSync to Android; grant Precise + Allow-all-the-time location.
  2. Dispatch: assign a trip to your driver acct + a deployable vehicle on a route, dated today.
  3. App: log in as that driver -> pass checklist -> Start Trip -> walk / ride. Click "Fit to buses".
  4. Writes gate on move >=25m / pax change / 60s heartbeat — move a bit before expecting a new point.
- Keep passenger-counter active (`trip_count_{tripId}` in SecureStorage). No key -> `LocationTrackingService` stops logging.

### P4 — Harden
- **Retention ✅** — `TelemetryRetentionService` (PR #26, merged) prunes daily; window via `Telemetry:RetentionMinutes` (default 1440).
- **Timezone ✅** — bounded read + retention cutoffs use `DateTime.UtcNow` (PostgREST reads naive filter strings as UTC; `PhClock.Now` landed 8h ahead -> empty map / over-delete). Fixed `80b713c`.
- Pending: confirm/add a `timestamp` index on `telemetry_data`.
- Pending: `telemetry_data` is anon-writable (same publishable-key class as `users`; `users` higher priority — `plan.md` §5).
- Pending: confirm the sim toggle is admin-only (not reachable by drivers / anon).

Status: **P0 ✅  P1 ✅ (incident cleanup)  P2 ✅  P3 viewport ✅ / phone test pending  P4 retention+tz ✅, index/security pending.**

---

## 5. WARNING — P1 is destructive

P1 permanently deletes rows from `trips` and `telemetry_data`. Cannot be undone.

- Back up FIRST (Supabase export, or `CREATE TABLE trips_bak AS SELECT * FROM trips;` + `telemetry_data_bak`).
- Run step 1 (SELECT) and eyeball: rows must be only the rogue Active/no-start trips, NO real ones.
- Telemetry delete is scoped by sim `trip_id` — phone telemetry (on real trips) must NOT appear in the delete set. Spot-check that your phone test trips have `actual_start_time` set.
- Only then run steps 2 -> 3.

---

## 6. Map clarification — no force-to-route

Common worry: "if I'm far from BGC, will the map drag me onto a bus route?" **No.**
- The pin uses the **exact** lat/lng from the telemetry row. No snapping, no geofence, no route-pull anywhere in the code.
- Route polylines are decoration only.
- The only route coupling is the **camera**: the map fits its view to the BGC routes, so a far pin is correct but **off-screen** until you pan/zoom (P3 viewport fix).

---

## 7. Open questions / decisions
- Sim toggle UI: hidden button where? (Fleet Map page admin corner? a dev-only route?)
- Viewport: build the permanent "Fit to buses" control now, or defer to after a manual test?
- Retention policy for `telemetry_data` (e.g. delete rows older than N days)?
- Tag telemetry too (`is_simulated` on `telemetry_data`), or rely on trip-tag join (current plan)?
