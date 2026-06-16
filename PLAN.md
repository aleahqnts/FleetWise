# FleetWise Driver App — Plan (AppDev version)

Driver-facing mobile app for the FleetWise fleet system. This version aligns to the
AppDev subject (C# + Supabase) and uses **manual** passenger counting (camera-based
counting is deferred to the capstone). The existing repo is the operator **web dashboard**
(ASP.NET Core MVC); this plan covers the separate **driver mobile app**.

## Architecture (locked)

| Decision | Choice |
|---|---|
| Framework | .NET MAUI **Blazor Hybrid** (Razor UI, C#) |
| Repo/structure | **Same git repo**, new sibling folder `FleetWiseMobile/`, **own** `.slnx`, own branch (`feature/driver-app`). Do NOT nest inside the `FleetWise/` web project. |
| Backend | **Shared** Supabase DB (same as dashboard) |
| Data access | **Direct** `supabase-csharp` SDK (demo-grade; use the **anon** key) |
| Auth | Mirror `FleetWise/Services/AuthService.cs` — query `users`, verify with `PasswordHasher`, filter `role_id=2` |
| Models | **Duplicate** ~8 model classes into the MAUI project (web app untouched) |
| Counting | **+1** boarding counter with **-1** undo -> `total_boarded` -> revenue |
| Connectivity | **Online + crash-safety**: mirror count to device storage each tap; push to Supabase at Start/End + light timer |
| Checklist gate | **Warn-but-allow** (flagged -> `checklist_status='Failed'`, warn, still start) |
| Scope | **Core loop only** — Login, Home, Checklist, Trip lifecycle, Trips history. No Notifications/Profile/Forgot-Password |
| Run | **Windows** target for dev, **Android** phone for demo (prereq: VS2022 + .NET MAUI workload) |

## Trip status vocabulary (must match dashboard)

Stored `trips.trip_status` values used by the system:
`Pending`, `Not Yet Started`, `Assignment Issue`, `Active`, `Completed`.

- App writes **`Active`** on Start Trip (+ `actual_start_time`).
- App writes **`Completed`** on End Trip (+ `actual_end_time`, finalize totals).
- "Ready to start" state = `Not Yet Started`. Checklist mandatory before Start Trip.

## Database delta

```sql
alter table trips
  add column total_boarded     int  not null default 0,
  add column actual_start_time timestamptz,
  add column actual_end_time   timestamptz;   -- estimated_revenue already exists

create table fare_config (
  id int primary key default 1,
  standard_fare numeric(10,2) not null,
  updated_at timestamptz default now()
);
insert into fare_config (id, standard_fare) values (1, 15.00);
```

**Seed (step 0):** one driver `users` row (`role_id=2`, `account_status='Activated'`,
hashed password), a `routes` row, a `vehicles` row, and 1–2 `trips` for that driver
dated today. The dispatch page is read-only for trips — nothing in the UI creates them.

## Build order (each phase runnable on Windows)

0. **Setup** — VS2022 + MAUI workload -> new Blazor Hybrid project in `FleetWiseMobile/`
   -> add `supabase-csharp` NuGet -> copy models -> configure Supabase client
   -> run schema delta + seed. *Verify:* app launches, reads a trip.
1. **Login** — mirror `ValidateAsync`; persist session in `SecureStorage`. *Verify:* log in as seed driver.
2. **Home** — today's assignment card (trip where `driver_id=me`, status `Pending`/`Not Yet Started`);
   availability toggle (upsert `driver_availability`, like dashboard `UpdateDriverAvailability`).
3. **Checklist** — 5-section form -> insert `bus_checklist` with JSON dicts + status; warn if flagged.
   *Verify:* submit unlocks Start Trip.
4. **Trip lifecycle** — Start Trip -> `Active` + `actual_start_time`; Active screen (+1/-1,
   live `revenue = total_boarded * standard_fare`, shift timer, local mirror);
   End Trip (confirm) -> `Completed` + `actual_end_time` + persist totals.
5. **Trips history** — read-only list of your completed trips.
6. **Polish + deploy to Android phone** for the demo.

## Gotchas

- **Vehicle-status coupling:** dashboard derives vehicle display-status from the checklist, but
  `SyncTripStatuses` reads a stored `vehicles.vehicle_status`. On checklist submit, verify whether
  the app must also write `vehicles.vehicle_status` (`Ready to Deploy`/`Flagged`) to stay consistent.
- **Security (known, demo-grade):** direct SDK ships the Supabase key in the APK, and the client-side
  password check reads `password_hash`, so RLS can't lock the `users` table without breaking login.
  OK for a graded demo; the **backend-API** path is the capstone fix.

## Models to duplicate into the MAUI project

`UserModel`, `Trip`, `BusChecklist`, `DriverAvailability`, `BusRoute`, `VehicleModel`, `Role`
(+ new `FareConfig`). All use `Postgrest.Models.BaseModel` — works unchanged in MAUI.
