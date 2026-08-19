# FleetWise QA Plan & Findings

> Local working doc — NOT committed to the repo. Captures the QA pass so the manual
> tests (1–15) and the throwaway tooling can be resumed/recreated later.
> Date of pass: 2026-06-23.

## Status

- **Bug fix (synced):** FleetMap permission gating — see below. Committed to the repo.
- **Test project + DB audit script:** built, run green, then **deleted** (not synced, per decision).
  Recreate from the snippets in this doc if needed.
- **Manual tests 1–15:** NOT done yet — resume later.

---

## Bugs / inconsistencies found

### 1. [FIXED + SYNCED] Dashboard Fleet Map widget broke for dashboard-only roles
`wwwroot/js/dashboard-fleetmap.js` fetches `/FleetMap/Positions` + `/FleetMap/Routes`.
Putting `[RequirePermission("routes")]` on the whole `FleetMapController` made a role with
`dashboard=true, routes=false` get a 302→login HTML on those fetches → empty map card.
**Fix:** attribute moved to the `FleetMap.Index` (page) action only; the read-only data
endpoints (`Positions`/`Routes`/`Stops`) stay open to any authed user. Admin/Dispatcher have
`routes` so were never affected — only a custom dashboard-only role.

### 2. [OPEN — manual] Duplicate user accounts (data hygiene, not a code bug)
Same person, multiple rows:
- "Aleah Quintos" — user_id **9** and **14**
- "Aleah Mae Quintos" — user_id **13** and **15**
- "Chester Alcanzarin" — user_id **16** (Driver) and **10** (Dispatcher)
Clutters the Users tab + the dispatch driver pool. Decide which to keep, delete the rest.
(Deleting a user that owns trips/checklists/logs may orphan those — check first.)

### 3. [BY DESIGN — note for testers] Permission claims are login-time
Toggling a role's permission only applies after that user re-logs (claims issued at sign-in).
Not a bug; don't read it as one during testing.

### 4. [MINOR] Gated endpoints answer fetch() with 302→login HTML, not 403 JSON
Cosmetic. The only real cross-page case was #1, now fixed.

DB invariants checked clean: no ghost Active trips, no vehicle double-live, no stuck
On-Trip vehicles, all activated users have a hash, all role_ids valid (1/2/3), no orphan
maintenance_notes, no dangling vehicle refs in trips/logs.

---

## Manual test matrix (1–15) — TODO

| # | Scenario | Steps | Expected |
|---|---|---|---|
| 1 | Driver web block | login web w/ driver creds | rejected ("Invalid email or password") |
| 2 | Operator mobile block | login app w/ admin/dispatcher | rejected |
| 3 | New-user temp pw | create user → login `@Temp123` | forced Set Password; no tab reachable until changed; can't reuse `@Temp123` |
| 4 | Perm nav (after re-login) | toggle Dispatcher reports OFF, save, re-login | Reports link gone; `/Reports` → redirect Dashboard |
| 5 | Perm access direct | dispatcher → type `/Users` | bounced to Dashboard |
| 6 | Resolve closes all | bus w/ 2 open incidents → Resolve & Return to Ready | both close, badge Ready, inspection Resolved, items gone |
| 7 | On-Trip self-heal | bus stuck "On Trip", no active trip | shows Ready in registry |
| 8 | Add Trip override | double-book driver → Create | 2-step override modal → creates |
| 9 | Reassign override | reassign onto a conflict | override modal → saves |
| 10 | Remove trip | reassign, clear BOTH bus+driver → Save | "Remove this trip?" → trip deleted |
| 11 | Maintenance buttons | Resolve / Return to Service | custom modal (not browser confirm), note recorded |
| 12 | History grouping | bus w/ multiple incidents | each lifecycle in its own shaded block |
| 13 | Message backlog | create new driver, login app | sees only post-creation broadcasts/route msgs |
| 14 | Op-day boundary | check dispatch/dashboard ~5:55am vs 6:05am | trips roll to new service day correctly |
| 15 | Manage Roles modal | open it | wide, minimal scroll |

---

## Throwaway tooling (deleted — recreate if needed)

### A. DB invariant audit (`scripts/db-audit.sh`)
Bash + curl against Supabase REST using the **publishable/anon key** (client-safe, read-only
checks). Checks, each printing `ok`/`FAIL`:
1. No ghost Active trips: `trip_status=Active AND actual_start_time IS NULL AND is_simulated=false`
2. No vehicle on >1 Active trip
3. No vehicle `vehicle_status` in (On Trip/OnTrip/Active) without a backing Active trip
4. Every Activated user has a `password_hash`
5. Every user `role_id` in (1,2,3)
6. No `maintenance_notes.log_id` without a parent log (orphans)
7. `maintenance_logs.vehicle_id` references a real vehicle
8. `trips.vehicle_id` references a real vehicle
9. Duplicate-name users (hygiene)

Endpoints: `GET {url}/rest/v1/{table}?select=...` with headers `apikey` + `Authorization: Bearer`.
Supabase URL: `https://vrtluruqaxutecydbrsq.supabase.co`.

### C. xUnit project (`FleetWise.Tests`, net10.0) — 27 tests, all green
Required source hooks (also reverted, re-add if recreating):
- `FleetWise.csproj`: `<ItemGroup><InternalsVisibleTo Include="FleetWise.Tests" /></ItemGroup>`
- `PhClock`: extract `internal static DateTime OperationalDayFor(DateTime now)` (pure), have
  `OperationalDay` call it.
- `VehiclesController`: make these `internal static` (were `private static`):
  `NonTripStatus`, `DeriveInspectionSections`, `DeriveInspectionBadge`, `NormalizeMaintenance`,
  `FormatMaintenanceEntry`.

Test coverage:
- **PhClock.OperationalDayFor:** 05:59→prev day, 00:30→prev, 06:00→today, 14:00→today, 23:59→today.
- **PasswordPolicy:** `TemporaryPassword == "@Temp123"`, `MustChangeClaim == "pwd_temp"`.
- **NonTripStatus:** OnTrip/On Trip/Active/Flagged → "Ready to Deploy"; Pending/Ready/null pass through.
- **DeriveInspectionBadge:** Failed→Flagged, Passed→Passed, Pending→Pending, ""→Pending.
- **NormalizeMaintenance:** Under Repair→same, No Issues/Resolved→No Issues, ""/null→Needs Attention.
- **DeriveInspectionSections:** groups failed items by section, excludes all-pass sections,
  rephrases negatives (e.g. "No fluid leaks under bus"→"Fluid leak under bus").
- **FormatMaintenanceEntry:** resolved log → IsResolved, "Resolved", issue summary, resolved date;
  open log → status + remarks fallback.

Limitation: DB-bound logic (conflict validation, ResolveIncident, override, RemoveTrip) is NOT
unit-tested — needs extracting pure logic out of the Supabase-wired controllers (a refactor).
Cover those via the manual matrix above for now.
