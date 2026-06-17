# FleetWise — User Management, Fleet Map & Vehicles Implementation Plan (Supabase edition)

> **Owner:** Franz (User Management + Fleet Map + Vehicles modules)
> **Branch:** `feature/vehicle`
> **Date:** 2026-06-13 (re-sequenced so User Management ships first, and corrected against the real mockup screenshots); **2026-06-17 — Vehicles module added to Franz's scope (Blocks 14–17, §2.8), moved over from Jhuztin.**
> **Status:** Re-baselined onto the database stack the team leader (Aleah) actually pushed — **Supabase (PostgreSQL) via `supabase-csharp`, not SQL Server / ASP.NET Identity.** Re-sequenced so **User Management ships first**: Blocks 0–4 are Auth + User Management, Blocks 5–13 are the Fleet Map. Blocks 2–4 were also corrected against the actual Figure 24–27 screenshots — the manuscript prose described a richer design (route assignment, driver-detail fields, per-module mobile permissions) than the mockups actually show; **the mockups win**. Delivered as 14 independently-reviewable blocks (Block 0–13). One block reviewed at a time before implementation — see §4.0 for the list and §9 for execution order.

---

## Table of Contents

1. [Context — what we're building and why](#1-context--what-were-building-and-why)
2. [Thought process — the design decisions and why](#2-thought-process--the-design-decisions-and-why)
3. [Architecture overview](#3-architecture-overview)
4. [Implementation blocks](#4-implementation-blocks)
5. [Files touched (summary)](#5-files-touched-summary)
6. [Database & seeding workflow (Franz's part)](#6-database--seeding-workflow-franzs-part)
7. [Heads-up for teammates — what changes under them](#7-heads-up-for-teammates--what-changes-under-them)
8. [Risks and mitigations](#8-risks-and-mitigations)
9. [Execution order and dependencies](#9-execution-order-and-dependencies)
10. [Verification](#10-verification)

---

## 1. Context — what we're building and why

### 1.1 The project

FleetWise is the team's capstone: a real-time bus fleet monitoring system. It has two clients — a **web operator dashboard** (ASP.NET Core MVC, .NET 10, this repository) used by administrators and dispatchers, and a **driver mobile application** (Chester's part, separate). The system's core promise per the manuscript is live visibility: where every bus is, how full it is, and how much revenue it is generating, in real time.

The specification comes from two documents:

- **`AppDev (1).md`** — the ERD defining 9 tables: `users`, `roles`, `vehicles`, `routes`, `bus_checklist`, `maintenance_logs`, `trips`, `messages`, `telemetry_data`. This is the agreed data contract for the whole team.
- **`G7_Manuscript (Chapter1-3)`** — Chapters 1–3 with the GUI mockups. The figures defining *this plan's* scope:
  - **Figure 17** — Fleet Map: a full-screen interactive map with route/status drop-down filters and live bus markers.
  - **Figure 18** — hover: pointing at a bus marker shows a quick-view tooltip with vital transit data.
  - **Figure 19** — click: clicking a bus marker opens a side panel showing assigned driver, Bus ID, route, status, passenger count, occupancy %, and automated revenue estimation; the map also plots designated stop markers.
  - **Figure 24** — User Management page: header row with a **Roles filter dropdown**, a **search box**, and **Add User** / **Manage Roles** buttons; a table of **Name, Email, Role, Status, Actions** (Actions = **Edit** only — there is no separate Activate/Deactivate button on the table).
  - **Figure 25** — Manage Roles: a left-hand **list of roles** with a **"+ Add Role"** button; selecting a role shows a right-hand panel with **Role Name** and **Access Level**, a grid of **7 web permission toggles** (Dashboard, Fleet Map, Vehicles, Dispatch, Analytics, Reports, Users), and **1 mobile permission toggle** ("Full Access").
  - **Figure 26** — Add New User: First/Middle/Last Name, Email, an **auto-generated read-only "Initial Password"**, and a **Role** dropdown. No route assignment, no license/contact "driver details", no password-visibility toggle.
  - **Figure 27** — Edit User: the same profile fields as Figure 26 plus a **Role** dropdown and an **Account Status** dropdown (Activated/Deactivated) — **this dropdown is how activation/deactivation works**, not a table button.
  - **The Vehicles mockups** (Operator → Vehicles, added to Franz's scope on 2026-06-17) — four screens:
    - **Vehicles registry** — a "Vehicles" page with top-bar **Route / Type / Status / Issues (Vehicle Condition)** filters and a search box; three summary cards (**Total Vehicles**, **Flagged Vehicles**, **Scheduled Maintenance**); an **+ Add Vehicle** button; and a **Vehicle Information** table — **Vehicle ID** (id over plate number, two lines), **Vehicle Type**, **Route**, a **Status** badge (Ready to Deploy / Pending / Flagged), a **Maintenance** badge (No Issues / Needs Attention / Under Repair), and **Actions** (**View** + **Edit**).
    - **Add Vehicle modal** — Vehicle Profile only: Vehicle ID, Plate Number, Vehicle Type (dropdown), Route (dropdown); Cancel / Add Vehicle.
    - **View Vehicle Details modal** (titled with the unit id, e.g. "BUS-02") — read-only: **Vehicle Profile** (Plate Number, Vehicle Type, Route), the latest driver **Inspection Log** (Reported By, Time of Report, Issue, a **yellow-highlighted Remarks box**, and a Flagged badge), and the **Maintenance Log** (current-status badge, Issue Summary, and a dated history of log entries).
    - **Edit Vehicle modal** — **Vehicle Profile** (Vehicle ID read-only, Plate Number, Vehicle Type, Route) plus **Maintenance Log Details** (Date Reported, Issue Summary, **Change Status** dropdown, **Verified by** input); Cancel / Save Changes.
  - The development-tools section names **Leaflet.js or Google Maps API** as the approved mapping library.

### 1.2 Team assignments

| Member | Modules | Relationship to this plan |
|---|---|---|
| **Aleah (team leader)** | Operator — Dashboard, Reports; **set up the Supabase database** | Already wired the Supabase client and modeled all 9 ERD tables; her Dashboard *reads* the trips/telemetry data this plan brings to life |
| Jhuztin | Operator — Dispatch | Builds Dispatch on the `routes` / `trips` Supabase tables (Vehicle Management moved to Franz on 2026-06-17) |
| **Franz (this plan)** | **Operator — User Management, Fleet Map, Vehicle Management** | — |
| Chester | Driver Mobile Application (manual passenger counting for now) | Driver accounts created by this module are his login identities; his app will eventually *write* the telemetry this plan's map reads |

User Management creates the accounts everything else authenticates as, and the Fleet Map consumes data nearly every other module produces — so several decisions below (§2.4 especially) exist to avoid blocking or duplicating teammates' work.

### 1.3 Current state of the codebase (verified by reading the code **and** querying the live Supabase database, not assumed)

- **.NET 10 MVC application.** `Program.cs` registers a **`Supabase.Client` singleton** (`supabase-csharp` 0.16.2 / `postgrest-csharp` 3.5.1) reading `Supabase:Url` / `Supabase:Key` from `appsettings.json`. **This is the database the app actually uses.**
- **All 9 ERD tables are modeled** as `postgrest-csharp` `BaseModel` classes in `Models/` (flat folder): `UserModel` (`users`), `Role` (`roles`), `Vehicle` (`vehicles`), `BusRoute` (`routes`), `Trip` (`trips`), `TelemetryData` (`telemetry_data`), `MaintenanceLog`, `BusChecklist`, `Message`. Each maps snake_case columns via `[Column(...)]`.
- **The Supabase tables exist but are EMPTY.** Querying the live project (`vrtluruqaxutecydbrsq`) returns `200` with `0` rows for every table; the model columns are confirmed real (`select=user_id,...` succeeds, a bogus column returns `400`). **So the schema is already in place — the missing piece is seed data.**
- **Login is a hardcoded placeholder.** `HomeController` GET renders the login page (via `Views/Shared/_LoginLayout.cshtml`); its POST checks the literal `admin@fleetwise.com` / `admin123` and redirects to Dashboard. `LoginViewModel` is a plain `{ Email, Password }` class — **not** ASP.NET Identity.
- **A dead SQL Server + ASP.NET Identity stack is still wired but unused.** `Program.cs` also calls `AddDbContext<ApplicationDbContext>(UseSqlServer(...))` + `AddDefaultIdentity<IdentityUser>`, `appsettings.json` still has a LocalDB `DefaultConnection`, `Areas/Identity` and the EF Identity migration still exist, and `_LoginPartial.cshtml` still injects `SignInManager<IdentityUser>`. **None of it is used by the real app flow.** Removing it is part of Block 0.
- **Module controllers are mostly empty scaffolds** (`UsersController`, `FleetMapController`, `VehiclesController`, `DispatchController`, `ReportsController` just `return View()`), **except** Aleah's `DashboardController`, which is a complete, working example of reading Supabase via `_supabase.From<Trip>()...Get()` — the pattern this plan follows everywhere.
- **The UI shell is finished and good.** `Views/Shared/_Sidebar.cshtml` implements the collapsed/expand-on-hover sidebar with inline SVG icons and a teal theme (`--fw-teal: #4AADA8`, `--fw-teal-light: #E8F6F5`); layout, login styling (`wwwroot/css/login.css`), Bootstrap 5 + jQuery + jquery-validation are all in `wwwroot/lib`. New pages adopt these theme variables.

### 1.4 Guiding principles

Franz asked for **KISS** (prefer the boring, obvious solution), **YAGNI** (don't build what isn't needed yet), and **SOLID**. Throughout §2, each decision names which principle drove it — and §2.7 is explicit about where we *stop*, because over-engineering is itself a KISS violation.

---

## 2. Thought process — the design decisions and why

This section is the reasoning record: what was considered, what was rejected, and why. If a panelist or teammate asks "why did you do it this way?", the answer is here.

### 2.1 Authentication: custom login against the Supabase `users` table — remove the dead ASP.NET Identity stack

**The situation.** The project was scaffolded with ASP.NET Identity on SQL Server, and an earlier version of this plan was going to *extend* Identity. But the team leader took the project a different way: she connected **Supabase** and modeled a custom `users` table that already carries `password_hash`, `role_id`, `email_address`, and `account_status`. The app's real login is a hardcoded placeholder in `HomeController`. So the genuine fork is no longer "extend Identity vs. hand-build tables" — that's settled. It is now: **make login real, the Supabase way.**

**Decision (confirmed with Franz): one database, custom cookie authentication over the `users` table.**

- **Remove the dead Identity / SQL Server stack** (`AddDefaultIdentity`, `AddDbContext`/`UseSqlServer`, the `DefaultConnection`, `Data/ApplicationDbContext.cs`, the EF Identity migration, the `Areas/Identity` scaffolding, the `_LoginPartial` Identity injection, and the unused EF/Identity/SQL-Server NuGet packages). Keeping two parallel "user" systems — one the app uses, one it ignores — is a correctness hazard and pure confusion. KISS: one source of truth for users, the Supabase `users` table.
- **Authenticate against `users`.** Login looks up the row by `email_address`, verifies the supplied password against `password_hash`, and on success issues a **cookie** via ASP.NET Core's cookie authentication (`AddAuthentication().AddCookie()` + `HttpContext.SignInAsync`), carrying the user's id, name, and **role** as claims. No JWT, no SignalR, no Identity — the minimum that gives us a real, role-aware session.
- **Don't hand-roll crypto.** We hash and verify with **`Microsoft.AspNetCore.Identity.PasswordHasher<T>`** — a standalone, well-tested PBKDF2 hasher available *without* the full Identity system. This is the one piece of Identity worth keeping: writing our own salted hash is exactly the security-sensitive wheel-reinvention KISS warns against. The seeder (Block 0) produces `password_hash` values with the same hasher, so seeded and UI-created accounts verify identically.

**Why not Supabase Auth (its managed `auth.users` product)?** It's more secure out of the box, but it's a *second* identity store separate from the ERD's `users` table — we'd have to keep the two linked, and the ERD/mockups (temporary passwords, account_status, role_id) are clearly written around *our* `users` table. Adopting Supabase Auth would mean either abandoning the ERD's user design or maintaining a bridge. YAGNI + consistency with the leader's schema → authenticate against `users` directly.

**The accepted trade-off.** Custom auth means we own session/lockout behavior. For a capstone with a known user set, cookie auth with a hashed password is sufficient; advanced features (lockout, password reset email, 2FA) are explicitly out of scope (YAGNI) and can be added later without changing the table.

### 2.2 Roles: build Manage Roles exactly as mocked up, hold the YAGNI line on enforcement

**The tension.** Figure 25 shows a Manage Roles screen with a role list, a "+ Add Role" button, and a permission-toggle grid, backed by `roles.web_permissions` / `mobile_permissions`. Pure YAGNI says seed three fixed roles and skip creation. But **Franz chose to match Figure 25**, because the mockups are part of the manuscript the panel compares against. The mockup *is* the requirement here.

**The permission shape, read directly off the mockup (the manuscript prose describes something richer — the mockup wins):**

- **`web_permissions`** is a flat map of **7** module → boolean toggles — the 6 sidebar pages **plus "Analytics"**:
  ```json
  { "Dashboard": true, "FleetMap": true, "Vehicles": false, "Dispatch": false, "Analytics": false, "Reports": true, "Users": false }
  ```
  Six of these match the sidebar 1:1; **"Analytics" has no controller or sidebar entry yet**, but it's in the mockup's toggle grid regardless, so we store it faithfully (the JSON column has room for it at no cost) — it is simply inert until an Analytics page exists. Flagged here so nobody is confused later by a toggle that does nothing.
- **`mobile_permissions`** is much simpler than the manuscript's per-module list (`Checklist`/`Trips`/`Messages`) — the mockup shows **one** toggle:
  ```json
  { "FullAccess": true }
  ```

**Role names.** The mockups aren't fully consistent with each other (Figure 24's status badges read "Administrator/Dispatcher/Driver"; Figure 25's role list shows "Admin/Driver") or with the ERD's example ("Admin, Driver, Operator"). We seed **`Admin`, `Dispatcher`, `Driver`** as a pragmatic reconciliation across all three — it's just data, a one-row edit later if the team prefers different names.

**"+ Add Role" — an inferred minimal form.** The mockup shows the button but not the resulting create form. We infer the smallest thing consistent with the edit panel already shown: Role Name + Access Level text inputs, plus the same 7 web + 1 mobile toggles, all defaulting **off**. Add and Edit are visually the same panel — no separate "create wizard".

**What we still don't build (YAGNI):**

- We store and round-trip these toggles faithfully, but we **do not** build a permission *enforcement* engine yet. Now that auth exists (§2.1), enforcement *could* be layered on with `[Authorize(Roles=...)]` or a claims check — but module-level permission gating beyond role is speculative until the team agrees the rules. The JSON columns mean enforcement can be added later **without a schema change**. (What Block 1 *does* enforce immediately: you must be logged in to reach operator pages, and the logged-in user's role travels in the cookie.)
- Roles get **no hard delete**: a role with users assigned can't simply vanish, and the mockup doesn't show deletion. Add/edit only.

### 2.3 Fleet Map data: simulated telemetry writer + JS polling, rendered with Leaflet.js

**The problem.** The map's purpose is showing *live, moving* buses — but no data source exists yet (no IoT hardware; Chester's app does manual counting, no GPS streaming), and the `telemetry_data` table is currently empty. A map with frozen dots proves nothing at the defense.

**Chosen approach — simulated telemetry writer + polling:**

A `BackgroundService` (`TelemetrySimulator`) advances each active bus along its route geometry every 5 seconds and **inserts ordinary rows into the Supabase `telemetry_data` table** — via `_supabase.From<TelemetryData>().Insert(...)`, the exact table the ERD defines for real hardware. The browser polls a JSON endpoint every 5 seconds and moves the markers.

**Why this is architecturally right, not just a demo trick.** The read path — `FleetMapController.Positions` querying "latest telemetry row per active trip" — *never knows the data is fake*. The simulator is a stand-in **producer** behind the same table contract that real IoT devices or Chester's app will use later. Cut-over day = turn off one hosted-service registration and point the real producer at the same `telemetry_data` table. Nothing in the controller, DTOs, or JavaScript changes. That is Dependency Inversion at the data boundary: consumers depend on the `telemetry_data` contract, not on who writes it.

- **Why polling, not SignalR/WebSockets.** With ~4 buses updating every 5 s and a handful of operators, polling delivers the identical experience at a fraction of the moving parts (no hub, no client lib, no reconnection logic). YAGNI. Swapping transports later touches only the endpoint and `fleetmap.js`.
- **Why Leaflet over Google Maps.** The manuscript permits either. Leaflet + OpenStreetMap needs **no API key, no billing, no quotas** — which matters for a capstone demoed from arbitrary networks months later, where a forgotten Google billing setup would break the demo. Leaflet's marker/tooltip/popup API maps one-to-one onto Figures 17–19.
- **Why 5 seconds.** Fast enough that motion is visible and the demo feels live; slow enough that Supabase and the dev machine never feel it. One constant in one place.

**One pragmatic note on Supabase write volume.** Inserting one row per active bus every 5 s is fine for a demo, but it accumulates rows in the cloud database. The simulator runs **Development-only**, and we keep the cadence at 5 s; a one-line "delete telemetry older than N hours" cleanup can be added to the tick if the table ever gets noisy (deferred — YAGNI).

### 2.4 Who owns the `vehicles` / `routes` / `trips` data? (cross-team decision)

**Already settled by the leader.** The Fleet Map can't exist without vehicles, routes, and trips — a bus marker *is* a join of telemetry → trip → vehicle/route/driver. The leader has **already modeled these tables in Supabase and created the model classes** (`Vehicle`, `BusRoute`, `Trip`). So Franz does **not** create these models — he *reuses* them, exactly as Aleah's `DashboardController` already does. **Franz now owns the Vehicles controller/views** (Blocks 14–17, §2.8); Jhuztin keeps **Dispatch** (this plan does not touch `DispatchController`). If a module needs extra columns, the owner adds them in Supabase + the model class.

**One geometry need not yet in the tables: route waypoints and stops.** The mockups show buses moving *along roads* (Figure 17) and "designated stop markers" plotted on the map (Figures 18–19). That geometry — an ordered polyline of waypoints and a list of named stops — has to live somewhere, and the `routes` model as pushed has only `route_name`, `origin`, `destination`, timestamps. Options: a separate `route_points` table (normalized, but adds a table and we'd never query points individually), or **two JSON/text columns on `routes`** (`waypoints_json`, `stops_json`) read whole per route. The ERD already uses JSON-ish columns (`roles.web_permissions`), so JSON columns are stylistically consistent and the smallest footprint:

```json
// waypoints_json — ordered path the bus follows
[ {"lat": 13.7565, "lng": 121.0583}, {"lat": 13.7591, "lng": 121.0612}, ... ]
// stops_json — named markers shown on the map
[ {"name": "Grand Terminal", "lat": 13.7565, "lng": 121.0583}, ... ]
```

These two columns are **the only schema change this plan introduces**, and it happens in **Block 5** (Map Shell & Base Layer — the first Fleet Map block), **not Block 0**. That keeps Block 0 (the prerequisite for User Management, which now ships first) free of any schema dependency. Because the leader owns the schema, the agreed flow (§2.5) is: Franz proposes the `ALTER TABLE routes ADD COLUMN ...` SQL, the leader (or Franz, with her okay) runs it in the Supabase SQL Editor, and the `BusRoute` model gains the two matching properties. Flagged loudly in §7.

### 2.5 Database changes go through the Supabase SQL Editor; seeding is done in code

The old SQL-Server/EF-migration/SSMS workflow is **gone** with the Identity stack. The Supabase reality:

- **The schema already exists** (all 9 tables, verified). Franz writes **no migrations** for existing tables, and **Block 0 makes zero schema changes** — it only removes dead code and seeds data, so it has no dependency on the leader running anything first.
- **The one additive change** (`routes.waypoints_json`, `routes.stops_json`, §2.4) is applied **in Block 5**, the first Fleet Map block, as a small **SQL snippet run in the Supabase dashboard's SQL Editor** — the leader controls the shared cloud schema, so this is coordinated with her. Claude provides the exact `ALTER TABLE` text; nobody runs a tool that mutates the database automatically.
- **The empty tables need seed data**, and that is done **in code** via a `DbSeeder` using the Supabase client:
  - Why code, not a SQL `INSERT` script: seeded users need real `password_hash` values (PBKDF2 via `PasswordHasher`, §2.1), which are impractical and error-prone to hand-write in SQL. `PasswordHasher.HashPassword(...)` produces them correctly, and the same hasher verifies them at login.
  - The seeder is **idempotent**: each block checks existence (e.g. "any roles?", "user with this email?") before inserting, so running the app twice never duplicates data.
  - It is wrapped in try/catch that logs a clear warning and continues if Supabase is unreachable — the app must still boot.

### 2.6 UI approach: server-rendered MVC + Bootstrap modals, not a SPA

The User Management mockups are modal-driven CRUD over a table — the classic server-rendered shape. Pattern: GET renders the page with data; each modal is a normal `<form method="post">`; POST actions redirect back with a `TempData` success/error alert (Post-Redirect-Get — refresh-safe). Bootstrap 5 and jquery-validation are already in `wwwroot/lib`, so client-side validation comes from the existing `_ValidationScriptsPartial` for free. This matches Aleah's existing MVC pages — consistency wins (KISS). The **one** legitimately JS-heavy page is the Fleet Map (map panning, marker animation, live polling), which gets a dedicated `fleetmap.js` (Leaflet from CDN, vanilla `fetch`, no build step).

### 2.7 Where SOLID shows up — and where we deliberately stop

| Principle | Where it appears |
|---|---|
| **S**ingle Responsibility | Controllers handle HTTP only (bind → call → redirect/JSON). Auth/session in an `AuthService`; simulation in `Services/TelemetrySimulator`; fare math in `Services/FareCalculator`; seeding in `Data/DbSeeder`. |
| **O**pen/Closed | New permission modules are added by editing one module list, not by schema changes; new roles are data, not code. |
| **L**iskov | n/a-heavy here — we favor composition over inheritance with the Supabase models. |
| **I**nterface Segregation | Controllers receive exactly the services they use. The map endpoint exposes a purpose-built `BusPositionDto`, not raw rows. |
| **D**ependency Inversion | The map's read path depends on the `telemetry_data` *table contract*, not on which producer (simulator, IoT, mobile app) writes it. Auth depends on `PasswordHasher`'s abstraction, not bespoke crypto. |

**Where we deliberately stop:** no repository/unit-of-work wrapper over the Supabase client (it is already a thin data gateway); no interfaces for single-implementation services (`FareCalculator` gets an interface the day a second implementation exists, not before); no mediator/CQRS/clean-architecture rings (two modules, nine tables — the cost is paid now, the benefit never arrives at this scale). Interfaces-on-everything is SOLID cosplay, not SOLID.

### 2.8 Vehicles module: derive the two badges, don't add status columns; one tiny additive change

Franz's scope grew on 2026-06-17 to include the **Vehicles page** (previously Jhuztin's). It's four screens — a registry table with summary cards + filters, and **Add / View-Details / Edit** modals — all read/curate over the existing `vehicles`, `bus_checklist`, and `maintenance_logs` tables. The decisions that matter:

- **Status reads `vehicles.vehicle_status` directly; Maintenance is derived (KISS, no new column).** The DB's `vehicle_status` column is a Postgres enum (`vehicle_status_enum`) whose labels are **Title-Case: `Ready to Deploy`, `Flagged`, `Pending`, `On Trip`** (verified against live data — earlier drafts of this plan wrote them lowercase, which is wrong; inserting a lowercase label fails with `22P02 invalid input value for enum`). Block 14 reads it straight for the **Status** badge. The **Maintenance** badge (No Issues / Needs Attention / Under Repair) is derived from the vehicle's latest **unresolved `maintenance_logs` row** (or "No Issues" when none is open) — which is why the mockup shows Status and Maintenance varying independently (e.g. *Flagged / No Issues*, *Ready to Deploy / Needs Attention*).
- **One shared column, no clash with Fleet Map.** Fleet Map (Block 10) reads the *same* `vehicle_status` and normalizes it for the map — `On Trip` → a live bus marker; the readiness values (`Ready to Deploy` / `Flagged` / `Pending`) → not-currently-driving. Both modules agree on one vocabulary, so there's nothing to reconcile: the Vehicles page shows the raw value, the Fleet Map maps it to its live labels. (The `bus_checklist` inspections still power the View modal's separate **Inspection Log**, Block 16 — Chester's mobile app writes those, a producer/consumer split like telemetry, §2.3.)
- **The one additive schema change:** `maintenance_logs.verified_by` — the Edit modal's "Verified by" field has no column today. Added via the **Supabase SQL Editor** (coordinated with Aleah, §2.5), mirroring Block 5's route columns. Everything else reuses existing columns; `last_maintenance_date` already exists for the prose's "update when a bus returns from the shop."
- **Reuse the established patterns, keep diffs small.** Server-rendered MVC + Bootstrap modals + PRG/`TempData` (§2.6), async `fetch`-partials for the table body and the View/Edit modal contents (the Block 2 addendum), the teal theme, and the same DataAnnotations validation as User Management. Nothing new is invented for this module.

**DB enum reference (verified against the live schema, 2026-06-17).** Several status columns are Postgres **enums**, so any value written must be a Title-Case label below *exactly* — a mismatch fails at insert/update with `22P02 invalid input value for enum` (this is what bit Block 15's first `Create`). Relevant to Franz's scope:

| Enum type | Column(s) | Labels |
|---|---|---|
| `vehicle_status_enum` | `vehicles.vehicle_status` | `Ready to Deploy`, `Flagged`, `Pending`, `On Trip` |
| `maintenance_status_enum` | `maintenance_logs.maintenance_status` | `Needs Attention`, `Under Repair`, `No Issues` |
| `checklist_status_enum` | `bus_checklist.checklist_status` | `Passed`, `Failed`, `Pending` |
| `account_status_enum` | `users.account_status` | `Activated`, `Deactivated` |
| `trip_status_enum` | `trips.trip_status` | `Not Yet Started`, `Active`, `Completed`, `Assignment Issue`, `Pending` |
| `priority_enum` | `messages.priority` | `Normal`, `High`, `Urgent` *(messages — out of Franz's scope)* |
| `target_audience_enum` | `messages.target_audience` | `All`, `Route`, `Driver` *(out of scope)* |

Two consequences for the remaining Vehicles blocks: (1) `maintenance_status` *already* holds the exact three Maintenance-badge labels, so Block 14's `NormalizeMaintenance` contains-matching is now redundant/defensive — and **Block 17's "Change Status" dropdown must offer exactly those three strings**. (2) `checklist_status` has **no "Flagged" value**, so Block 16's mockup "Flagged" inspection badge must be **derived** (`Failed` → Flagged), not read raw.

---

## 3. Architecture overview

### 3.1 Project layout after this work

```
FleetWise/
├── Models/                         (leader's Supabase models — REUSED, not recreated)
│   ├── UserModel.cs  Role.cs  Vehicle.cs  BusRoute.cs  Trip.cs
│   ├── TelemetryData.cs  MaintenanceLog.cs  BusChecklist.cs  Message.cs
│   ├── LoginViewModel.cs           (exists; reused by real auth)
│   └── ViewModels/
│       ├── UserListItemViewModel.cs / AddUserViewModel.cs / EditUserViewModel.cs / RoleFormViewModel.cs
│       └── BusPositionDto.cs / StopDto.cs
├── Services/
│   ├── AuthService.cs              (verify credentials, build claims)   ← Block 1
│   ├── FareCalculator.cs           (revenue = passengers × FareRate)    ← Block 8
│   └── TelemetrySimulator.cs       (BackgroundService, Development only) ← Block 9
├── Data/                            (DbSeeder.cs removed post-Block 0 — tables already seeded, see §4.0)
├── Controllers/
│   ├── HomeController.cs           (real login replaces hardcoded check) ← Block 1
│   ├── UsersController.cs          (list/filter/search + user & role CRUD) ← Blocks 2–4
│   └── FleetMapController.cs       (map page + Positions/Stops JSON endpoints) ← Blocks 5–13
├── Views/
│   ├── Users/Index.cshtml          (+ _AddUserModal, _EditUserModal, _ManageRolesModal)
│   └── FleetMap/Index.cshtml       (Leaflet map shell + side panel markup)
└── wwwroot/js/fleetmap.js          (markers, tooltip, side panel, 5 s polling)

REMOVED in Block 0 (dead SQL Server / Identity stack):
  Data/ApplicationDbContext.cs, Data/Migrations/*, Areas/Identity/*,
  AddDbContext/UseSqlServer + AddDefaultIdentity in Program.cs,
  ConnectionStrings:DefaultConnection, the EF/Identity/SqlServer NuGet packages,
  _LoginPartial.cshtml's SignInManager<IdentityUser> injection.
```

### 3.2 Data flow for the Fleet Map

```
TelemetrySimulator (dev only) ──Insert──▶ Supabase telemetry_data ◀──Insert── (future: IoT / Chester's app)
                                                  │
                          FleetMapController.Positions
                          (latest row per active trip, joined in C#:
                           trips → vehicles, routes, users)
                                                  │  JSON, polled every 5 s
                          wwwroot/js/fleetmap.js
                          (move markers in place, update tooltip + side panel)
```

Everything **above** the middle line is replaceable producers; everything **below** never changes when producers do.

### 3.3 Data flow for auth + User Management

```
Browser ──POST /Home/Index (email,pw)──▶ AuthService.ValidateAsync
                                          (users row → PasswordHasher.Verify → claims+cookie)
                                          └─▶ RedirectToAction Dashboard

Browser ──GET /Users?role=Driver&search=cruz──▶ UsersController.Index ──▶ _supabase.From<UserModel>()/<Role>()
Browser ──POST (modal form + antiforgery)─────▶ Create/Edit/CreateRole/UpdateRole
                                                 └─▶ redirect → Index + TempData alert (Post-Redirect-Get)
```

Note: there is **no separate `SetStatus` action** — Account Status is just a field on the Edit User form (Figure 27), submitted through the same `Edit` POST.

### 3.4 Delivery as 14 review blocks

Both diagrams are built incrementally. §4.0 gives the full block list, dependencies, and review status. **User Management ships first**: auth + User Management map onto Blocks 1–4; the Fleet Map diagram maps onto Blocks 5–13.

---

## 4. Implementation blocks

### 4.0 Blocks at a glance

| # | Block | Module | Depends on | Status |
|---|---|---|---|---|
| 0 | Cleanup & Seed Data | Shared | — | ✅ Done (seed data confirmed in Supabase; `users_user_id_seq`/`roles_role_id_seq` sequence desync fixed via `setval()` in the SQL Editor; `Data/DbSeeder.cs` and its `Program.cs` call were removed afterward — tables already hold data, idempotent re-seeding no longer needed. SQL Server/Identity removal reverted at Franz's request — `AddDefaultIdentity<IdentityUser>` stays, additive cookie auth added instead) |
| 1 | Custom Authentication (users table + cookie) | Auth | Block 0 | ✅ Built — pending manual test (login/logout, `[Authorize]` redirects) |
| 2 | User List, Filter & Search | User Mgmt | Block 1 | ✅ Built — refined for instant navigation (async row loading, see addendum) |
| 3 | Add/Edit User (incl. activate/deactivate) | User Mgmt | Block 2 | ✅ Built — pending manual test (sequence-desync fix applied, Add User should no longer 23505) |
| 4 | Manage Roles & Permissions | User Mgmt | Block 2 | ✅ Built — pending manual test (sequence-desync fix applied, Add Role should no longer 23505) |
| 5 | Map Shell, Base Layer & Route Geometry | Fleet Map | Block 0 | ✅ Done — 2 routes (Route 01 North Express blue, Route 02 South Line orange) with road-accurate geometry imported from recorded GPX tracks; legend & route colors updated to the 2 routes; `fitBounds` wired from `FleetMapController.Index()` so the map opens fitted to the routes |
| 6 | Stop Markers | Fleet Map | Block 5 | ✅ Done — `Stops` endpoint + `StopDto`; stops render as route-colored dots with name tooltips, each snapped onto its route polyline so the line passes through every stop |
| 7 | Route/Status Filters | Fleet Map | Block 5 | ✅ Built — pending manual test (shared `refetch()`: Route filter narrows polylines **and** stop markers via `/FleetMap/Stops?routeId=`; Status dropdown populated (On Trip/Idle/Offline) but inert until Block 11 wires `fetchPositions`) |
| 8 | Fare Calculator Service | Fleet Map | Block 0 | ✅ Built — `Services/FareCalculator.cs` (`Estimate(passengers) => passengers * FareRate`); `FleetWise:FareRate` = 15.00 in appsettings (defaults to 15.00 if absent); registered scoped in `Program.cs` |
| 9 | Telemetry Simulator | Fleet Map | Block 0 | ✅ Built — pending manual test (`Services/TelemetrySimulator.cs` `BackgroundService`, 5 s `PeriodicTimer`; per tick reads Active trips, advances each along its cached route polyline at 20–40 km/h with jitter, drifts `current_passengers` ±3 clamped to capacity, computes heading, inserts a `telemetry_data` row; geometry + per-trip cursor state cached in memory; no per-tick DI scope since only the singleton `Supabase.Client` is used; registered `AddHostedService` **Development-only** in `Program.cs`) |
| 10 | Positions Endpoint | Fleet Map | Blocks 8, 9 | ✅ Built — pending manual test (`FleetMapController.Positions(int? routeId, string? status)` + `Models/ViewModels/BusPositionDto.cs`; takes the latest `telemetry_data` row per Active trip, joins trip → vehicle/route/driver in C#, computes `occupancyPct` and `estimatedRevenue` (via `FareCalculator`) server-side, applies optional `routeId`/`status` filters; `status` normalized to "On Trip"/"Idle"/"Offline" from `vehicle_status`; `FareCalculator` injected into the controller. **Fix:** telemetry fetch now `.Order("timestamp", Descending)` — a plain `.Get()` is capped at 1000 rows and returns the *oldest*, so on a `telemetry_data` table with history the "latest per trip" was permanently stale and buses never moved; ordering newest-first resolves it. NOTE: `DashboardController` has the same un-ordered `.From<TelemetryData>().Get()` for passenger counts — same latent staleness, flag to Aleah) |
| 11 | Live Bus Markers & Polling | Fleet Map | Blocks 5, 10 | ✅ Built — pending manual test (`fleetmap.js` placeholder buses replaced by a 5 s `fetchPositions` poll of `/FleetMap/Positions`; markers keyed by `vehicleId`, **moved in place** via `setLatLng` + tooltip refresh, never recreated; buses absent from a response are removed; `refetch()` now also re-polls positions so the Route/Status filters narrow the live buses; a "Connection lost — retrying…" badge shows on fetch error and clears on resume. **Deviation from Step 11.1:** the route-colored **pill** marker (established in Block 5) is kept upright rather than CSS-rotated by heading — rotating a text pill would make the `BUS-01` label unreadable; heading is still produced by the simulator and surfaced in Positions for later use) |
| 12 | Hover Tooltip | Fleet Map | Block 11 | ✅ Built — pending manual test (Leaflet tooltip bound per bus in Block 11; extended to show **plate number** alongside bus id, route, status dot, and total passengers per Figure 18; `setTooltipContent` on each poll keeps a held hover live) |
| 13 | Click Side Panel | Fleet Map | Block 11 | ✅ Built — pending manual test (`#fmPanel` slide-in panel in `Index.cshtml`, **redesigned to match Franz's mockup exactly**: bold "BUS xx" title + ×, Route ## (blue) / Shift (orange badge) meta row, green-dot status, grey driver card w/ avatar + "Current Driver", two-column **Total Passengers** block, **Estimated Revenue** band showing `P ###.##`, and an italic "Last updated: N ago" line. Added **`shift`** to `BusPositionDto` (formatted "6AM – 12PM" from `Trip` shift times via `FormatShift`). Each marker stores its latest `_bus`; every poll refreshes the open panel live (incl. relative "last updated", timestamp parsed as UTC); close button + clicking another bus switches contents) |
| 14 | Vehicles Registry: List, Summary Cards, Filters & Search | Vehicles | Block 1 | ✅ Built — pending manual test (static UI now data-wired: `Index` builds card counts + dropdowns, `VehicleRows` returns the filtered `_VehicleRows` partial loaded async; Status normalized from `vehicle_status`, Maintenance derived from latest unresolved `maintenance_logs`; Route/Type/Status/Issues + search are query-string params) |
| 15 | Add Vehicle Modal | Vehicles | Block 14 | ✅ Built — pending manual test (`AddVehicleViewModel` + `Create` POST: dup `vehicle_id` check, defaults `capacity=50` / `vehicle_status="Ready to Deploy"` (Title-Case enum label), PRG+`TempData`; `_AddVehicleModal` now strongly-typed with real Type/Route dropdowns, validation spans, antiforgery; auto-reopens on failed POST) |
| 16 | View Vehicle Details Modal | Vehicles | Block 14 | ✅ Built — pending manual test (`GET Details(id)` fetch-partial → `_VehicleDetails`; View button wired to fetch per-vehicle; `BusChecklist` jsonb columns retyped to `Dictionary<string,string>`, Inspection "Flagged" derived from `Failed`) |
| 17 | Edit Vehicle Modal (incl. maintenance-log update) | Vehicles | Block 14 | ✅ Built — pending manual test **+ requires `ALTER TABLE maintenance_logs ADD verified_by` run in Supabase SQL Editor** (see addendum) |

Each block is self-contained: a goal, the steps, the files it touches, and a quick "Verify". Blocks are presented one at a time in §9's order; update the Status column as each is reviewed and built.

---

### Block 0 — Cleanup & Seed Data

**Goal.** Put the app on a clean Supabase-only footing and give **User Management (Blocks 1–4), which ships first,** plus the leader's Dashboard, real data to work with: remove the dead Identity/SQL Server stack and seed the empty tables. This is the **only** block that touches configuration and packages — it makes **zero schema changes** (the one schema change in this whole plan, the `routes` geometry columns, is Block 5's concern, see §2.4/§2.5, so it can't delay User Management).

**Step 0.1 — Remove the dead SQL Server / ASP.NET Identity stack.**
- `Program.cs`: delete `AddDbContext<ApplicationDbContext>(UseSqlServer(...))`, `AddDatabaseDeveloperPageExceptionFilter`, `AddDefaultIdentity<IdentityUser>...`, and `UseMigrationsEndPoint`. Keep the `Supabase.Client` registration. Add cookie auth here (the registration itself lands with Block 1 so `Program.cs` never references an auth service that doesn't exist yet — Block 0 only *removes*).
- Delete `Data/ApplicationDbContext.cs`, `Data/Migrations/*`, and the `Areas/Identity/*` scaffolding.
- `appsettings.json`: remove `ConnectionStrings:DefaultConnection`.
- `FleetWise.csproj`: remove the now-unused packages (`Microsoft.AspNetCore.Identity.EntityFrameworkCore`, `Identity.UI`, `EntityFrameworkCore.SqlServer`, `EntityFrameworkCore.Design`, `EntityFrameworkCore.Tools`, `Diagnostics.EntityFrameworkCore`). **Keep** `Microsoft.Extensions.Identity.Core` (or reference `Microsoft.AspNetCore.Identity`) **only** for `PasswordHasher<T>` — if pulling the whole Identity package is the simpler route, that's acceptable; the point is we use *only* the hasher.
- `Views/Shared/_LoginPartial.cshtml`: remove the `SignInManager<IdentityUser>`/`UserManager<IdentityUser>` injection. It is replaced (Block 1) by a claims-based check (`User.Identity.IsAuthenticated`) so the navbar greeting/logout work with cookie auth.

**Step 0.2 — `Data/DbSeeder.cs`** — idempotent, via the Supabase client; every block guarded by an existence check:
1. **Roles:** `Admin`, `Dispatcher`, `Driver` — each with an `access_level` string and the §2.2 permission JSON:
   - **Admin:** all 7 `web_permissions` true, `mobile_permissions.FullAccess = true`.
   - **Dispatcher:** `Dashboard`, `FleetMap`, `Vehicles`, `Dispatch`, `Reports` true; `Analytics`, `Users` false; `FullAccess` false.
   - **Driver:** all `web_permissions` false; `FullAccess = true`.
2. **Users (~6, matching Figure 24's roster):** `admin@fleetwise.com` (Admin — keeps the existing demo credential working), 2 Dispatchers, 3 Drivers — a mix of `account_status = "Activated"` and one `"Deactivated"` so the Block 2 table shows both badge colors from first boot. Each `password_hash` is produced by `PasswordHasher`. Credentials documented in the PR description, to be changed before any public demo.
3. **Routes:** two routes with just `route_name`, `origin`, `destination` and timestamps — **no geometry yet**; `waypoints_json`/`stops_json` are added and populated in Block 5, the first Fleet Map block.
4. **Vehicles:** four buses (`BUS-01`…`BUS-04`), capacities ~40–60, split across the two routes, `vehicle_status = "On Trip"` (Title-Case `vehicle_status_enum` label).
5. **Trips:** today's trips pairing each bus with a driver, `trip_status = "Active"` — the simulator (Block 9) only animates Active trips, so the map is alive as soon as Fleet Map work reaches that point.

`DbSeeder.SeedAsync` is called once after `app.Build()`, wrapped so a Supabase outage logs a warning and the app still boots (§2.5).

**Files touched**

| Action | Path |
|---|---|
| modify | `Program.cs` (remove Identity/SQL Server; seeder call) |
| modify | `appsettings.json` (remove `DefaultConnection`) |
| modify | `FleetWise.csproj` (drop EF/Identity/SqlServer packages; keep hasher) |
| modify | `Views/Shared/_LoginPartial.cshtml` (claims-based, no Identity) |
| delete | `Data/ApplicationDbContext.cs`, `Data/Migrations/*`, `Areas/Identity/*` |
| new | `Data/DbSeeder.cs` |

**Verify.** `dotnet build` compiles cleanly with no SQL Server/Identity references. App boots; in Supabase, `users`, `roles`, `routes`, `vehicles`, `trips` now contain the seeded rows. **⏸ This is the go-signal for every other block — most importantly, Block 1 (auth).**

---

### Block 1 — Custom Authentication (users table + cookie)

**Goal.** Replace the hardcoded login with real authentication against the Supabase `users` table, using cookie sessions that carry the user's role. This makes "create a user" (Block 3) actually grant a working login.

**Step 1.1 — `Services/AuthService.cs`.** `ValidateAsync(email, password)`: look up the `users` row by `email_address` via the Supabase client; if found and `account_status == "Activated"`, verify `password` against `password_hash` with `PasswordHasher<UserModel>.VerifyHashedPassword`; on success return the user (with its role name resolved from `roles`). One responsibility: credential verification.

**Step 1.2 — Cookie auth registration** in `Program.cs`: `AddAuthentication(CookieAuthenticationDefaults...).AddCookie(o => { o.LoginPath = "/"; o.AccessDeniedPath = "/"; })`. Add `app.UseAuthentication()` **before** `app.UseAuthorization()`.

**Step 1.3 — `HomeController` real login.** Replace the hardcoded check: GET renders the login view as today; POST calls `AuthService.ValidateAsync`, and on success builds a `ClaimsPrincipal` (NameIdentifier = `user_id`, Name = full name, Email, **Role** = role name) and calls `HttpContext.SignInAsync`, then redirects to Dashboard. On failure, the existing `ModelState` error path is kept. Add a `Logout` action (`SignOutAsync` → redirect to `/`).

**Step 1.4 — Protect operator pages.** Apply `[Authorize]` to the operator controllers (Dashboard, Users, FleetMap, Vehicles, Dispatch, Reports) so an unauthenticated visitor is bounced to the login page. `HomeController`'s login actions stay `[AllowAnonymous]`. (Role-level gating beyond "logged in" is deferred per §2.2.)

**Step 1.5 — Navbar.** `_LoginPartial.cshtml` (already de-Identitied in Block 0) shows the logged-in user's name and a Logout button when `User.Identity.IsAuthenticated`, else nothing.

**Files touched**

| Action | Path |
|---|---|
| new | `Services/AuthService.cs` |
| modify | `Program.cs` (cookie auth + `UseAuthentication`) |
| modify | `Controllers/HomeController.cs` (real login + Logout) |
| modify | `Controllers/{Dashboard,Users,FleetMap,Vehicles,Dispatch,Reports}Controller.cs` (`[Authorize]`) |
| modify | `Views/Shared/_LoginPartial.cshtml` |

**Verify.** Logging in with a seeded account (e.g. `admin@fleetwise.com`) lands on the Dashboard and the navbar greets the user; a wrong password shows the inline error; visiting `/Users` while logged out redirects to the login page; Logout returns to login and re-blocks the operator pages.

---

### Block 2 — User List, Filter & Search

**Goal.** Stand up `/Users` matching **Figure 24 exactly**: the `Index` action with role-filter and search as query parameters, and the table. The header's **Add User** / **Manage Roles** buttons and each row's **Edit** action render here but stay inert until Blocks 3–4. There is **no separate Activate/Deactivate button** — the mockup's only row action is Edit (status changes live inside the Edit modal, Block 3).

**Step 2.1 — `UsersController.Index(string? role, string? search)` (GET).** Inject `Supabase.Client`. Load `users` (and the `roles` list for every dropdown) via `_supabase.From<UserModel>()...Get()`; resolve each user's role name from the roles list in memory (small, fixed set — no N+1). Role filter and name/email search arrive as **query-string parameters** — bookmarkable, zero JS (KISS).

**Step 2.2 — `Models/ViewModels/UserListItemViewModel.cs`.** One table row, matching Figure 24's columns exactly: `UserId`, `FullName` (Last, First M.), `Email`, `RoleName`, `AccountStatus`. (No `LastLogin` column — the mockup doesn't show one; the `users.last_login` column still exists in the DB for later use, it's just not displayed here.)

**Step 2.3 — `Views/Users/Index.cshtml`** matching Figure 24, styled with the existing teal theme:
- **Header:** Roles filter dropdown (auto-submits on change), search box, and **Add User** / **Manage Roles** buttons (markup only — modals arrive in Blocks 3 and 4).
- **Table:** Name (Last, First M.), Email, Role, Status badge (green *Activated* / grey *Deactivated*), Actions (**Edit** only — rendered, wired in Block 3).

**Files touched**

| Action | Path |
|---|---|
| modify | `Controllers/UsersController.cs` (`Index`) |
| new | `Models/ViewModels/UserListItemViewModel.cs` |
| new | `Views/Users/Index.cshtml` |

**Verify.** `/Users` lists the seeded users with correct name/email/role/status formatting, matching Figure 24's columns exactly (no Last Login column); the Roles dropdown filters; search matches partial name and email. Buttons/Edit action are visible but inert.

**Addendum — instant navigation via async row loading (post-Block 4 refinement).** Sidebar navigation to `/Users` used to feel sluggish because the whole page (including the Supabase query) had to finish before anything rendered. `Index` now returns immediately with an empty model; `<tbody id="usersTableBody">` starts with a spinner placeholder row. A new `[HttpGet] UserRows(string? role, string? search)` action runs `BuildUserListAsync` and returns `PartialView("_UserRows", items)` (the `<tr>` markup extracted into `Views/Users/_UserRows.cshtml`). `Index.cshtml`'s `loadUserRows()` fetches that partial via `fetch('@Url.Action("UserRows","Users")' + window.location.search)` on `DOMContentLoaded` and swaps it into `#usersTableBody`. Net effect: clicking "Users" in the sidebar navigates instantly, with only the table body showing a brief spinner while data loads — still server-rendered HTML over `fetch`, not a SPA (§2.6 still holds).

---

### Block 3 — Add/Edit User (includes activation/deactivation)

**Goal.** The Add User and Edit User modals (Figures 26–27) and their POST actions. **There is no separate Activate/Deactivate block** — Figure 27's Account Status dropdown on the Edit modal *is* how activation/deactivation works, so it's built here. Establishes the Post-Redirect-Get + `TempData` pattern reused by Block 4.

**Step 3.1 — Two ViewModels, because Add and Edit genuinely differ (Figures 26 vs 27):**
- **`Models/ViewModels/AddUserViewModel.cs`**: `FirstName`, `MiddleName`, `LastName`, `Email`, `InitialPassword`, `RoleId`. **No status field** — new users are always created `Activated`.
- **`Models/ViewModels/EditUserViewModel.cs`**: `UserId`, `FirstName`, `MiddleName`, `LastName`, `Email`, `RoleId`, `AccountStatus`, `NewPassword` (optional). **No password field on Add beyond the auto-generated one, no "temp password" concept on Edit** — only an optional reset.

DataAnnotations mirror the ERD: `[Required]`, `[EmailAddress]`, `[StringLength(50)]` on name parts. Server-side `ModelState` validation **and** free client-side validation via the existing `_ValidationScriptsPartial`. Controllers never bind raw model rows from forms.

**Step 3.2 — `Create(AddUserViewModel)` (POST).** Build a `UserModel`, set `password_hash = PasswordHasher.HashPassword(...)` from `InitialPassword`, `account_status = "Activated"`, `role_id`, `created_at = UtcNow`; insert via `_supabase.From<UserModel>().Insert(...)`. **Reject duplicate email** with a pre-insert existence check (the Supabase unique constraint is the backstop) → error into `ModelState`, modal re-opens with input preserved.

**Step 3.3 — `Edit(EditUserViewModel)` (POST).** Update names/email/role/**`account_status`**; stamp `updated_at`. **Password is re-hashed only when `NewPassword` is non-empty** — editing must never silently wipe a password. Update via `_supabase.From<UserModel>().Where(u => u.UserId == id).Update(...)`. Changing `AccountStatus` to `Deactivated` here is the entire activate/deactivate feature — no extra endpoint, and (via Block 1) a deactivated user immediately can't log in.

**Step 3.4 — Modal views + wiring.**
- **`_AddUserModal.cshtml`** (Figure 26): First/Middle/Last, Email, an **"Initial Password"** field that is **read-only and auto-generated by JS when the modal opens** (e.g. a random 9-character string mixing letters/digits/symbols, matching the mockup's "IA2JK!0K5" style) — its value is submitted as a normal form field and hashed server-side in Step 3.2, Role dropdown. No status field, no route/driver-detail fields, no password-visibility toggle (the manuscript describes these; the mockup doesn't have them).
- **`_EditUserModal.cshtml`** (Figure 27): First/Middle/Last, Email, Role dropdown, **Account Status dropdown** (Activated/Deactivated), and an optional "New password (leave blank to keep current)". **One** modal serves every row: each Edit button carries the row's current values in `data-*` attributes, and ~15 lines of JS copy them in on open.
- Wire Block 2's **Add User** button; auto-reopen the relevant modal after a failed POST (a `TempData` flag + one line of JS).

Every POST here and after: `[ValidateAntiForgeryToken]`, then redirect to `Index` with `TempData["Success"]` / `["Error"]` (PRG — refresh-safe).

**Files touched**

| Action | Path |
|---|---|
| modify | `Controllers/UsersController.cs` (`Create`, `Edit`) |
| new | `Models/ViewModels/AddUserViewModel.cs`, `EditUserViewModel.cs` |
| new | `Views/Users/_AddUserModal.cshtml`, `_EditUserModal.cshtml` |
| modify | `Views/Users/Index.cshtml` (wire Add + Edit data-* + copy-in JS + password-generator JS) |

**Verify.** **Add User** auto-fills a generated Initial Password on modal open, then creates an account that appears immediately with a success alert **and can actually log in** with that password (Block 1); a duplicate email re-opens the modal with the error and input preserved; **Edit** changes fields and only re-hashes the password when one is typed; switching **Account Status to Deactivated** in Edit flips the Block 2 badge to grey and immediately blocks that account's login — reactivating restores both.

---

### Block 4 — Manage Roles & Permissions

**Goal.** The Manage Roles screen (Figure 25): role list, **"+ Add Role"**, `access_level`, and the permission toggle grid, backed by `CreateRole`/`UpdateRole`. This is the last User Management block.

**Step 4.1 — `Models/ViewModels/RoleFormViewModel.cs`** + the §2.2 permission JSON, read off the mockup (not the manuscript's longer lists):
- `WebPermissions`: flat `{"Module": bool}` map over exactly **7** keys — `Dashboard`, `FleetMap`, `Vehicles`, `Dispatch`, `Analytics`, `Reports`, `Users`.
- `MobilePermissions`: flat map over **1** key — `FullAccess`.

**Step 4.2 — `CreateRole` / `UpdateRole` (POST).** The permission checkbox grid binds to a dictionary serialized to the `web_permissions` / `mobile_permissions` columns. Duplicate role names rejected via a pre-insert check. **No hard delete** (§2.2). `[ValidateAntiForgeryToken]` + PRG.

**Step 4.3 — `_ManageRolesModal.cshtml`** (Figure 25):
- **Left:** role list (`Admin`, `Dispatcher`, `Driver` from the seed, plus any added) + **"+ Add Role"** button.
- **"+ Add Role"** opens the same right-hand panel as selecting an existing role, but blank: Role Name + Access Level text inputs, and all 7 web + 1 mobile toggles defaulting **off** (§2.2's inferred minimal create-form — the mockup doesn't show this state, this is the smallest thing consistent with the edit panel it does show).
- **Right (selected or new role):** Role Name, Access Level, the 7-toggle web grid, the 1-toggle mobile row. Save posts to Create/Update.
- New/edited roles appear immediately in the Block 2 filter and Block 3 dropdowns (all read `roles`). Same auto-reopen-on-failed-POST pattern as Block 3.

**Files touched**

| Action | Path |
|---|---|
| modify | `Controllers/UsersController.cs` (`CreateRole`, `UpdateRole`) |
| new | `Models/ViewModels/RoleFormViewModel.cs` |
| new | `Views/Users/_ManageRolesModal.cshtml` |
| modify | `Views/Users/Index.cshtml` (wire Manage Roles button) |

**Verify.** Modal lists `Admin`/`Dispatcher`/`Driver`; **"+ Add Role"** opens a blank panel with all toggles off; saving a new role with some toggles on persists, survives reload, and appears in the Block 2 filter and Block 3 Add/Edit dropdowns; toggles round-trip exactly — 7 keys in `web_permissions`, 1 key (`FullAccess`) in `mobile_permissions` (verify in Supabase: `select role_name, web_permissions, mobile_permissions from roles`). **⏸ This is the end of the User Management track — Blocks 5–13 (Fleet Map) can now begin.**

---

### Block 5 — Map Shell, Base Layer & Route Geometry

**Goal.** Stand up `/FleetMap`: apply **the one schema change in this whole plan** (§2.4 — `routes` geometry columns), populate the two seed routes' geometry, and render a Leaflet map filling the content area with OpenStreetMap tiles, initial view fitted to the seeded routes. No live bus data yet — the canvas every later Fleet Map block draws on.

**Step 5.1 — Add route geometry columns.** In the **Supabase SQL Editor** (coordinated with the leader, §2.5):
```sql
ALTER TABLE routes ADD COLUMN IF NOT EXISTS waypoints_json text;
ALTER TABLE routes ADD COLUMN IF NOT EXISTS stops_json    text;
```
Then add `WaypointsJson` / `StopsJson` `[Column(...)]` properties to `Models/BusRoute.cs`.

**Step 5.2 — Populate the seed routes' geometry.** The two routes from Block 0 (`route_name`/`origin`/`destination` only) get hand-picked, road-following `waypoints_json` polylines and 4–6 named stops each in `stops_json` (realistic local geography so the demo map looks credible). **Originally planned as a `DbSeeder` addition, but `DbSeeder.cs` was removed in Block 0** (tables already hold data, idempotent re-seeding no longer needed) — so this is now done the same way as Step 5.1: a one-time `UPDATE routes SET waypoints_json = '...', stops_json = '...' WHERE route_id = ...` for each of the two seed routes, run in the **Supabase SQL Editor** alongside the `ALTER TABLE`. Claude provides the exact `UPDATE` statements (with the real route ids and hand-picked coordinates); no code changes needed for this step.

**Step 5.3 — `FleetMapController.Index()`.** Load the seeded routes' coordinate bounds (from `waypoints_json`) so the view model passes an initial center/zoom.

**Step 5.4 — `Views/FleetMap/Index.cshtml`.** Leaflet 1.9.x from CDN + OSM tile layer; map fills the content area beside the sidebar (Figure 17's "full-screen interactive map"). Initial view fitted to the routes' bounds.

**Step 5.5 — `wwwroot/js/fleetmap.js`.** Map init only: `L.map(...)`, OSM tile layer, `fitBounds` from Step 5.3.

**Files touched**

| Action | Path |
|---|---|
| modify | `Models/BusRoute.cs` (add `WaypointsJson`, `StopsJson`) |
| modify | `Data/DbSeeder.cs` (populate geometry for the two seed routes) |
| modify | `Controllers/FleetMapController.cs` (`Index`) |
| new | `Views/FleetMap/Index.cshtml` |
| new | `wwwroot/js/fleetmap.js` (map init only) |
| (SQL) | `ALTER TABLE routes ...` run in Supabase SQL Editor |

**Verify.** In Supabase, `routes` has the two new columns populated for both seed routes; `/FleetMap` renders a full-screen map fitted to the seeded routes' area; OSM tiles load.

**Addendum — UI shell built first, then geometry wired in (Franz's request: build the UI of Block 5 first).** All of Steps 5.1–5.5 are now done. `/FleetMap` renders the full-screen Leaflet shell: a topbar with Search + Route/Status filter controls (`fm-*` classes mirroring the existing Users/Dashboard search-box and dropdown patterns), a full-bleed map pinned below the topbar and right of the sidebar (`.fm-page`, `position: fixed` so it ignores `.main-content`'s padding), and a bottom-left route-color legend.

**Two routes, not four.** The plan originally imagined four demo routes; in practice the map ships with **two real routes** — `Route 01 – North Express` (blue `#2563EB`) and `Route 02 – South Line` (orange `#F97316`) — each with road-accurate geometry. `Route 03`/`Route 04` rows still exist in the DB but carry `null` geometry and are not drawn. The legend and the `ROUTE_COLORS` map in `fleetmap.js` were trimmed to these two.

**Tile layer.** Tiles are plain **OpenStreetMap** (`tile.openstreetmap.org`) — switched from the earlier CyclOSM/CartoDB experiments to the clean default Mapnik style for legible road context. Still §2.3-compliant: no API key, no billing, no quotas.

**Geometry source — recorded GPX, not hand-picked coordinates.** Step 5.2's geometry was populated from **real GPX tracks** Franz recorded along each route (exported via GoogleMapsToGPX), parsed to `waypoints_json`. Route 02's full track (752 points) was simplified with Douglas–Peucker (~2 m tolerance) to 207 points to keep the `UPDATE` payload small enough to paste into the Supabase SQL Editor without truncation — a corruption issue we hit with the larger raw string. Each route's `stops_json` markers were then **projected onto the route polyline** so the rendered line passes exactly through every stop dot (the original landmark coordinates sat up to ~1.3 km off the recorded path, which is why dots and line didn't line up at first).

`wwwroot/js/fleetmap.js` also got a head start on Blocks 11/12: it places static placeholder bus markers (pill-shaped `divIcon`s colored per route) with a Leaflet tooltip matching Figure 18's "Route 01 | ●Active | Total Passengers: 11" layout. These are explicitly **static placeholders**: Block 11 replaces the hardcoded array with a `fetch('/FleetMap/Positions')` poll.

---

### Block 6 — Stop Markers

**Goal.** The `Stops` endpoint + the "designated stop markers" of Figures 18–19, parsed from each route's `stops_json`.

**Step 6.1 — `Models/ViewModels/StopDto.cs`.** `{ name, lat, lng, routeName }`.

**Step 6.2 — `GET Stops(int? routeId)`.** Parse `BusRoute.StopsJson`, optional `routeId` filter; return JSON.

**Step 6.3 — `fleetmap.js`.** On load, `fetch('/FleetMap/Stops')` and render each stop with a distinct smaller icon + name tooltip — visually separable from the bus markers (Block 11).

**Files touched**

| Action | Path |
|---|---|
| modify | `Controllers/FleetMapController.cs` (`Stops`) |
| new | `Models/ViewModels/StopDto.cs` |
| modify | `wwwroot/js/fleetmap.js` (stop rendering) |

**Verify.** Stop markers from the seeded `stops_json` appear with name tooltips, icon clearly distinct from buses.

---

### Block 7 — Route/Status Filters

**Goal.** The Route and Status dropdown filters from the Figure 17 top bar, wired to refetch on change.

**Step 7.1 — Top-bar markup** in `Index.cshtml`: a Route dropdown (from the seeded routes) and a Status dropdown (map-relevant trip/vehicle statuses, e.g. "On Trip", "Pending").

**Step 7.2 — `fleetmap.js`.** A shared `refetch()` reads both dropdowns, rebuilds the query string, and re-fetches `Stops` filtered by `routeId`. The **Status** filter has no effect on stops — its visible effect lands with Block 11 once `Positions` exists. Building the plumbing now means Block 11 only adds one fetch call to the same handler.

**Files touched**

| Action | Path |
|---|---|
| modify | `Views/FleetMap/Index.cshtml` (top bar) |
| modify | `wwwroot/js/fleetmap.js` (`refetch()` + change handlers) |

**Verify.** Changing the Route filter narrows the stop markers; the Status dropdown is present/changeable with no visible effect yet (expected).

---

### Block 8 — Fare Calculator Service

**Goal.** One small reusable service — `Estimate(passengers) => passengers * FareRate` — consumed by Block 10's Positions endpoint and available for Aleah's dashboard/reports.

**Step 8.1 — `Services/FareCalculator.cs`.** `Estimate(int passengers) => passengers * _fareRate`, rate bound from config:
```json
"FleetWise": { "FareRate": 15.00 }
```
Why a service for one multiplication? Two modules need the same number — the map's side panel (Block 13) and Aleah's revenue figures — and a hardcoded fare in two places is how they end up disagreeing in front of the panel. The appsettings key makes the fare a deployment decision.

**Step 8.2 — `Program.cs`.** Register `FareCalculator` (scoped).

**Files touched**

| Action | Path |
|---|---|
| new | `Services/FareCalculator.cs` |
| modify | `appsettings.json` (`FleetWise:FareRate`) |
| modify | `Program.cs` (register) |

**Verify.** Builds with `FareCalculator` resolvable; `Estimate(34)` at `FareRate=15.00` returns `510.00`.

---

### Block 9 — Telemetry Simulator

**Goal.** A `BackgroundService` faking live movement by **inserting `telemetry_data` rows into Supabase** every 5 s — the source Block 10's endpoint reads.

**Step 9.1 — `Services/TelemetrySimulator.cs`** (`BackgroundService`):
- Infinite loop on a 5-second `PeriodicTimer`. Each tick **creates a fresh DI scope** to resolve services — a hosted service is a singleton and must never capture scoped state (captive-dependency bug). The `Supabase.Client` is a singleton, so it can be used directly; per-tick scoping is for any scoped helpers.
- Per tick, for each `Trip` with `trip_status == "Active"`: load its route's waypoints (cached in memory after first read — geometry doesn't change mid-run); advance that trip's cursor along the polyline at a bus-like speed (~20–40 km/h with jitter), interpolating and looping at the end; drift `current_passengers` by a small random delta clamped to `[0, vehicle.capacity]`; compute `heading` from the current segment; **insert one `telemetry_data` row** via `_supabase.From<TelemetryData>().Insert(...)`.
- Per-trip cursors live in an in-memory dictionary; on restart, buses resume from the route start — fine for a simulator.

**Step 9.2 — `Program.cs`.** Register `AddHostedService<TelemetrySimulator>()` **only when `app.Environment.IsDevelopment()`** — so it can never run in production. Cut-over to real telemetry = delete one line (§2.3).

**Files touched**

| Action | Path |
|---|---|
| new | `Services/TelemetrySimulator.cs` |
| modify | `Program.cs` (register, Development only) |

**Verify.** With the app running, in Supabase `select * from telemetry_data order by timestamp desc limit 20` shows fresh rows every ~5 s with `latitude`/`longitude` advancing and `current_passengers` drifting.

---

### Block 10 — Positions Endpoint

**Goal.** The `Positions` JSON endpoint — the contract Block 11's markers, Block 12's tooltip, and Block 13's side panel consume. Depends on Block 8 (`FareCalculator`) and Block 9 (data).

**Step 10.1 — `Models/ViewModels/BusPositionDto.cs`.** Shape per Step 10.2.

**Step 10.2 — `GET Positions(int? routeId, string? status)`.** Fetch `telemetry_data` (and trips/vehicles/routes/users) via the Supabase client; in C#, take the **latest row per active `trip_id`** (group by trip, max timestamp), join trip → vehicle, route, driver, apply the optional filters, project to:
```json
[{
  "vehicleId": "BUS-01", "plateNumber": "ABC-1234",
  "routeName": "Route 1 — Terminal ↔ City Center",
  "driverName": "Juan Dela Cruz", "status": "On Trip",
  "lat": 13.7565, "lng": 121.0583, "heading": 245.0, "speed": 32.5,
  "passengers": 34, "capacity": 50, "occupancyPct": 68,
  "estimatedRevenue": 510.00, "timestamp": "2026-06-13T09:30:05Z"
}]
```
`occupancyPct` and `estimatedRevenue` (via `FareCalculator`) are computed **server-side** so every consumer shows identical numbers. (If telemetry volume ever makes the "latest per trip" grouping heavy, it can move to a Postgres view/RPC later — deferred, YAGNI.)

**Files touched**

| Action | Path |
|---|---|
| modify | `Controllers/FleetMapController.cs` (`Positions`) |
| new | `Models/ViewModels/BusPositionDto.cs` |

**Verify.** Hitting `/FleetMap/Positions` returns a JSON array whose `lat`/`lng`/`heading`/`passengers`/`occupancyPct`/`estimatedRevenue` change between requests ~5 s apart; `routeId`/`status` narrow it.

---

### Block 11 — Live Bus Markers & Polling

**Goal.** Render bus markers from `Positions`, poll every 5 s, move markers in place. Figure 17 becomes *live*, and Block 7's filters become fully wired.

**Step 11.1 — Bus markers.** Leaflet `divIcon` with a bus glyph, CSS-rotated by `heading` so buses point along travel.

**Step 11.2 — Polling.** `setInterval(fetchPositions, 5000)`. Existing markers are **moved in place** (`setLatLng` + rotation) rather than recreated — no flicker, open tooltips/panel survive. Buses absent from a response (trip ended) are removed. Fetch errors show a small "connection lost" badge and the loop keeps trying.

**Step 11.3 — Wire Block 7's filters.** `refetch()` now also calls `fetchPositions` with the same params — changing either filter immediately narrows the visible buses.

**Files touched**

| Action | Path |
|---|---|
| modify | `wwwroot/js/fleetmap.js` (markers, polling, filter wiring) |
| modify | `Views/FleetMap/Index.cshtml` or CSS (bus icon styles) |

**Verify.** Bus markers appear and visibly move every ~5 s, icons pointing along travel; filters narrow the set; stopping the app stops movement (data comes from the DB, not the browser); the "connection lost" badge appears when the server is down and clears on resume.

---

### Block 12 — Hover Tooltip

**Goal.** Figure 18 — hovering a bus shows a quick-view tooltip (plate, route, passenger count).

**Step 12.1 — Tooltip binding.** Bind a Leaflet tooltip to each bus marker; content updates each poll cycle so a held hover shows live numbers.

**Files touched**

| Action | Path |
|---|---|
| modify | `wwwroot/js/fleetmap.js` (tooltip) |

**Verify.** Hovering shows plate, route, current passengers; holding across a poll updates the count.

---

### Block 13 — Click Side Panel

**Goal.** Figure 19 — clicking a bus opens a side panel (driver, Bus ID, route, status, passengers + occupancy %, estimated revenue), updating live while open.

**Step 13.1 — Side panel markup + CSS** in `Index.cshtml`: a slide-in panel, hidden by default, teal-themed.

**Step 13.2 — Click handler + live update.** Clicking a bus opens the panel and fills driver, Bus ID, route, status, passenger count, an occupancy bar (`occupancyPct`), and revenue. While open, each poll updates the selected bus's fields. Close button hides it; clicking another bus switches contents.

**Files touched**

| Action | Path |
|---|---|
| modify | `Views/FleetMap/Index.cshtml` (panel markup + CSS) |
| modify | `wwwroot/js/fleetmap.js` (click, live update, close) |

**Verify.** Clicking opens the panel with correct data and a live occupancy bar; numbers tick while open; clicking another bus updates it; close hides it.

---

### Block 14 — Vehicles Registry: List, Summary Cards, Filters & Search

**Goal.** Stand up `/Vehicles` matching the **Vehicles registry mockup** exactly: a "Vehicles" page with three summary cards (**Total Vehicles**, **Flagged Vehicles**, **Scheduled Maintenance**), a top-bar **Route / Type / Status / Issues (Vehicle Condition)** filter set + search box, an **+ Add Vehicle** button, and the **Vehicle Information** table (Vehicle ID + plate, Vehicle Type, Route, Status badge, Maintenance badge, Actions = **View** / **Edit**). The View/Edit/Add buttons render here but stay inert until Blocks 15–17. This is the foundation the three modals hang off — the Vehicles equivalent of Block 2.

**Step 14.1 — `VehiclesController.Index(string? route, string? type, string? status, string? condition, string? search)` (GET).** Inject `Supabase.Client`. Load `vehicles` (the **Status** badge is just `vehicle_status`), `routes` (for the Route filter + name resolution), and the latest *unresolved* `maintenance_logs` per vehicle (drives the **Maintenance** badge), all via `_supabase.From<...>()...Get()` and joined in C# (small fixed sets — no N+1). Filters and `search` arrive as **query-string params** — bookmarkable, zero JS (KISS, mirrors Block 2).

**Step 14.2 — Badges (§2.8 — no new column).**
- **Status**: read `vehicles.vehicle_status` directly — the `vehicle_status_enum` column holds the Title-Case labels `Ready to Deploy` / `Flagged` / `Pending` / `On Trip`. The badge renders that value (the registry covers all four; the mockup screenshots just happen to show three). The Status filter binds to the same column.
- **Maintenance** (No Issues / Needs Attention / Under Repair): the latest unresolved `maintenance_logs.maintenance_status`, or **No Issues** when none is open.

**Step 14.3 — Summary cards.**
- **Total Vehicles** = vehicle count.
- **Flagged Vehicles** = count where `vehicle_status = "flagged"`.
- **Scheduled Maintenance** = count with an open maintenance log in **Under Repair** (booked into the shop).

**Step 14.4 — ViewModels.** `Models/VehicleListItemViewModel.cs` — one row: `VehicleId`, `PlateNumber`, `VehicleType`, `RouteName`, `Status`, `Maintenance`. `Models/VehiclesIndexViewModel.cs` — the rows, the three card counts, and the dropdown option lists (routes, distinct types, the fixed Status/Condition sets).

**Step 14.5 — `Views/Vehicles/Index.cshtml`** matching the registry mockup, teal theme reused (`fm-*`/Users patterns):
- **Top bar:** Route, Type, Status, and Issues (Vehicle Condition) dropdowns (auto-submit on change) + Search box.
- **Three summary cards** with the mockup's icons and numbers.
- **+ Add Vehicle** button (teal) on a row with the Search box.
- **Vehicle Information** table: Vehicle ID (id over plate, two lines), Vehicle Type, Route, **Status** badge (Ready to Deploy = teal, On Trip = blue, Pending = yellow, Flagged = red/pink), **Maintenance** badge (No Issues = green, Needs Attention = red/pink, Under Repair = yellow), Actions = **View** + **Edit** (rendered, wired in 16/17).

**Step 14.6 — async row loading** (reuse Block 2's refinement). `Index` returns immediately with an empty model; the `<tbody>` starts with a spinner; a `[HttpGet] VehicleRows(...)` action returns `PartialView("_VehicleRows", items)` fetched on `DOMContentLoaded` with the current query string — sidebar navigation feels instant, still server-rendered HTML over `fetch` (not a SPA, §2.6 holds).

**Files touched**

| Action | Path |
|---|---|
| modify | `Controllers/VehiclesController.cs` (`Index`, `VehicleRows`) |
| new | `Models/VehicleListItemViewModel.cs`, `Models/VehiclesIndexViewModel.cs` |
| new | `Views/Vehicles/Index.cshtml`, `Views/Vehicles/_VehicleRows.cshtml` |

**Verify.** `/Vehicles` matches the registry mockup; the three cards show correct counts; the Route/Type/Status/Issues filters and search each narrow the table; Status and Maintenance badges are colored per the mockup; View/Edit/Add are visible but inert.

**Addendum — all Vehicles UI built first, static (Franz's request, 2026-06-17).** Like Block 5, the full Vehicles UI shell (Blocks 14–17) was built before any data wiring so it can be eyeballed in the browser. Delivered:
- `Views/Vehicles/Index.cshtml` — the registry rendered with a **`vh-*` class namespace** (own `<style>` block + teal theme, no collision with the Users page's `us-*`). Topbar carries **all four** filters (Route / Type / Status / Issues) via `@@section TopbarContent`; three summary cards (Total 32 / Flagged 10 / Scheduled Maintenance 2); **+ Add Vehicle** + Search toolbar; and the **Vehicle Information** table with **9 static placeholder rows** (BUS-01…BUS-09) matching the mockup — two-line Vehicle ID/plate, Status badges (Ready to Deploy = teal, **On Trip = blue**, Pending = yellow, Flagged = red), Maintenance badges (No Issues = green, Needs Attention = red, Under Repair = yellow), and **View / Edit** action pills.
- `Views/Vehicles/_AddVehicleModal.cshtml`, `_ViewVehicleModal.cshtml`, `_EditVehicleModal.cshtml` — the three modals (see Blocks 15–17).
- `VehiclesController.Index()` is unchanged (still `return View()`); the rows/cards are hardcoded in the view. `dotnet build` is clean.

**Data-wiring done (Block 14 proper, 2026-06-17).** The static rows/cards are now live:
- `VehiclesController.Index(route, type, status, condition, search)` loads `vehicles` + `routes` + `maintenance_logs` once (`LoadVehicleDataAsync`), computes the three card counts over **all** vehicles (unaffected by filters), and builds the four dropdown option lists (Route from `routes`; Type = distinct `vehicle_type`; Status/Issues = fixed vocabularies). Rows return empty — loaded async.
- `[HttpGet] VehicleRows(...)` runs `BuildRowsAsync`, applies the four filters + search (Vehicle ID / plate), and returns `PartialView("_VehicleRows", items)`; `Views/Vehicles/_VehicleRows.cshtml` holds the `<tr>` markup + badge-class logic (mirrors `_UserRows`). `Index.cshtml` fetches it on `DOMContentLoaded` into `#vehiclesTableBody` (spinner placeholder) — instant nav, still server-rendered HTML over `fetch`.
- **Badges (§2.8):** Status from `DisplayStatus(vehicle_status)` (case-insensitive; `OnTrip`/`On Trip`/`Active` → "On Trip", matches FleetMap's vocabulary); Maintenance from the latest **unresolved** (`resolved_at == null`) `maintenance_logs` row via `NormalizeMaintenance` (contains "Repair" → Under Repair; "No Issue"/"Resolved" → No Issues; any other open log → Needs Attention), else "No Issues".
- **Filter/search forms combined** the Block 2 way: the topbar filter form carries `search` as a hidden input and each `<select>` auto-submits (`onchange`); the toolbar search form carries the four filter values as hidden inputs — so any one control preserves the rest of the query string.

**Still open (Blocks 15–17):** Add `Create` POST, View `Details` fetch, Edit `EditForm`/`Edit` + `verified_by` column. The View/Edit buttons now carry `data-vehicle-id` (ready for 16/17) but still open the static modals. **Deviation noted (unchanged):** the registry mockup screenshot shows only Route + Status in the topbar; the build keeps all four filters per the written spec — trim to two if the team prefers the screenshot literally.

---

### Block 15 — Add Vehicle Modal

**Goal.** The **Add Vehicle modal** and its POST — Vehicle Profile only (Vehicle ID, Plate Number, Vehicle Type, Route), establishing the PRG/`TempData` pattern the other Vehicle modals reuse (same as Block 3).

**Step 15.1 — `Models/AddVehicleViewModel.cs`:** `VehicleId`, `PlateNumber`, `VehicleType`, `RouteId`. DataAnnotations mirror the schema (`[Required]`, `[StringLength]`). Vehicle Type and Route are dropdowns; server-side `ModelState` + free client-side validation via `_ValidationScriptsPartial`.

**Step 15.2 — `Create(AddVehicleViewModel)` (POST).** Build a `Vehicle`, set `created_at = UtcNow`, a sensible default `capacity` (not in the mockup — e.g. 50), and `vehicle_status = "ready to deploy"` (new units start deployable). **Reject a duplicate `vehicle_id`** (the PK) with a pre-insert existence check (the Supabase PK constraint is the backstop) → error into `ModelState`, modal re-opens with input preserved. Insert via `_supabase.From<Vehicle>().Insert(...)`. `[ValidateAntiForgeryToken]` + redirect to `Index` with `TempData["Success"]`/`["Error"]`.

**Step 15.3 — `_AddVehicleModal.cshtml`** matching the mockup: centered **"Add Vehicle"** title + ×, a grey **"Vehicle Profile"** section bar, the four fields, then **Cancel** (outline) + **Add Vehicle** (teal). Wire Block 14's **+ Add Vehicle** button; auto-reopen the modal after a failed POST (TempData flag + one line of JS).

**Files touched**

| Action | Path |
|---|---|
| modify | `Controllers/VehiclesController.cs` (`Create`) |
| new | `Models/AddVehicleViewModel.cs` |
| new | `Views/Vehicles/_AddVehicleModal.cshtml` |
| modify | `Views/Vehicles/Index.cshtml` (wire + Add Vehicle button) |

**Verify.** **Add Vehicle** creates a unit that appears in the table immediately with a success alert; a duplicate Vehicle ID re-opens the modal with the error and input preserved; Cancel closes without saving.

**Addendum — static UI done (2026-06-17).** `_AddVehicleModal.cshtml` is built to the mockup (centered "Add Vehicle" title + ×, grey "Vehicle Profile" bar, the four fields with Type/Route as dropdowns, Cancel + Add Vehicle).

**Data-wiring done (Block 15 proper, 2026-06-17).** The modal now posts for real:
- `Models/AddVehicleViewModel.cs` — `VehicleId`, `PlateNumber`, `VehicleType`, `RouteId` with `[Required]`/`[StringLength]`/`[Range]` DataAnnotations + `[Display]` names.
- `VehiclesController.Create(AddVehicleViewModel)` — `[ValidateAntiForgeryToken]`; pre-insert duplicate-`vehicle_id` check → `ModelState` error; builds a `Vehicle` with `capacity = 50` and `vehicle_status = "ready to deploy"` defaults, `created_at = UtcNow`; inserts via `_supabase.From<Vehicle>().Insert(...)`; PRG → `Index` with `TempData["Success"]`. On invalid `ModelState`, `ReRenderIndexAsync` returns the registry view with the modal re-opened (`ViewBag.OpenModal = "AddVehicle"`) and input/errors preserved.
- `_AddVehicleModal.cshtml` is now `@model AddVehicleViewModel`, `asp-action="Create"` (auto-antiforgery), with real **Type** (`{Bus, Van}` ∪ existing distinct types) and **Route** (`RouteId`→`RouteName`) dropdowns from `ViewBag`, plus `asp-validation-for` spans.
- `Index.cshtml` passes the bound model to the partial, sets dropdown `ViewBag`s via `SetModalViewData`, adds the auto-reopen-on-failed-POST JS, and a `.vh-field-error` style.

**Two insert gotchas fixed during manual testing (2026-06-17):**
- **Enum casing:** `vehicle_status` is the `vehicle_status_enum`; the seed default label is Title-Case **`Ready to Deploy`** (an earlier lowercase `"ready to deploy"` failed with `22P02`). See the §2.8 enum reference.
- **PK must be inserted:** postgrest-csharp's `[PrimaryKey]` defaults to **`shouldInsert: false`** (assumes a DB-generated key, true for the serial `user_id`/`role_id`). But `vehicles.vehicle_id` is a user-entered `varchar` with no default, so it was silently omitted and Postgres rejected the null (`23502`). Fixed by `[PrimaryKey("vehicle_id", true)]` on `VehicleModel`. **Heads-up for Block 17:** any future model whose PK is user/app-supplied (not a serial) needs `shouldInsert: true`.

---

### Block 16 — View Vehicle Details Modal

**Goal.** The **View Vehicle Details modal** (titled with the unit id, e.g. "BUS-02"): a read-only diagnostic view combining **Vehicle Profile**, the latest driver **Inspection Log**, and the **Maintenance Log** history — Figure-faithful to the details mockup.

**Step 16.1 — `Models/VehicleDetailsViewModel.cs`:** Profile (`PlateNumber`, `VehicleType`, `RouteName`); Inspection (`ReportedBy` driver name, `TimeOfReport`, `Issue`, `Remarks`, `InspectionBadge`) from the latest `bus_checklist` (+ driver join to `users`); Maintenance (`CurrentStatus`, `IssueSummary`, and a list of entries formatted `MM/DD/YY – ML-## – Status`) from `maintenance_logs`.

**Step 16.2 — `GET Details(string id)`.** Load the vehicle, its latest `bus_checklist` (+ driver), and its `maintenance_logs` ordered newest-first; return `PartialView("_VehicleDetails", vm)`. Fetch-partial (Block 2 addendum) — fresh data, no heavy page payload. "Issue" on the inspection is derived from the failed checklist categories; "Remarks" comes from the checklist `notes`.

**Step 16.3 — `_VehicleDetails.cshtml`** matching the mockup: bold unit-id title + ×; **Vehicle Profile** (Plate Number, Vehicle Type, Route); **Inspection Log** with a red **Flagged** badge (Reported By, Time of Report, Issue, and a **yellow-highlighted Remarks box**); **Maintenance Log** with a status badge (Current Status, Issue Summary, and the dated timeline entries, newest first).

**Step 16.4 — wire Block 14's View button:** on click, `fetch('/Vehicles/Details?id=' + id)` into the modal body and show it.

**Files touched**

| Action | Path |
|---|---|
| modify | `Controllers/VehiclesController.cs` (`Details`) |
| new | `Models/VehicleDetailsViewModel.cs` |
| new | `Views/Vehicles/_VehicleDetails.cshtml` |
| modify | `Views/Vehicles/Index.cshtml` (wire View button + fetch JS) |

**Verify.** Clicking **View** opens the modal populated from `bus_checklist` + `maintenance_logs` for that vehicle; Remarks render in the yellow box; the maintenance timeline lists entries newest-first; the title shows the unit id.

**Addendum — static UI done (2026-06-17).** `_ViewVehicleModal.cshtml` is built to the mockup with a hardcoded **BUS-02 example** (Vehicle Profile, Inspection Log with a Flagged badge + yellow Remarks box, Maintenance Log with a Needs-Attention badge + dated timeline). Because it's static, *every* row's **View** opens the same example. **Still to do:** `VehicleDetailsViewModel`, the `GET Details(id)` fetch-partial, and wiring each View button to fetch its vehicle's real data.

**Schema prep done ahead of wiring (2026-06-17, from a full nullability sweep of the live schema).** Two model corrections so Block 16's `bus_checklist` read won't crash:
- `BusChecklist`'s five inspection columns (`exterior_inspection`, `engine_compartment`, `interior_inspection`, `brake_safety`, `passenger_systems`) are **`jsonb`**, not text — flat `{ "item": "Pass"/"Fail" }` maps — and were mistyped as `string` (would throw on the leading `{`, like `MaintenanceLog.IssueDetails`). Retyped to `Dictionary<string, string>`. **Step 16.2's "Issue derived from failed checklist categories" = the entries whose value isn't `"Pass"`.**
- `checklist_status_enum` has no `Flagged` value (only `Passed`/`Failed`/`Pending`), so Step 16.3's red **Flagged** inspection badge is **derived** (`Failed` → Flagged), confirmed against the schema. Verified clean: `bus_checklist.driver_id` and `submitted_at` are **NOT NULL**, so `BusChecklist`'s `int`/`DateTime` typing there is correct. (The earlier `MaintenanceLog.checklist_id` → `int?` fix was the only nullability mismatch in the whole schema sweep.)

---

### Block 17 — Edit Vehicle Modal (incl. maintenance-log update)

**Goal.** The **Edit Vehicle modal**: update the **Vehicle Profile** and the vehicle's current **Maintenance Log** (Change Status, Verified by) — Figure-faithful to the edit mockup.

**Step 17.1 — Schema (the Vehicles module's only change).** The "Verified by" field has no column on `maintenance_logs`. Add one additive column via the **Supabase SQL Editor** (coordinated with Aleah, §2.5/§2.8):
```sql
ALTER TABLE maintenance_logs ADD COLUMN IF NOT EXISTS verified_by text;
```
and add a matching `VerifiedBy` `[Column("verified_by")]` to `Models/MaintenanceLogModel.cs`.

**Step 17.2 — `Models/EditVehicleViewModel.cs`:** `VehicleId` (read-only — the PK), `PlateNumber`, `VehicleType`, `RouteId`, plus maintenance fields `LogId`, `MaintenanceStatus` (Change Status), `VerifiedBy`. Date Reported + Issue Summary are display-only (not posted).

**Step 17.3 — `GET EditForm(string id)`.** Returns `_EditVehicleModal` populated with the profile + the latest maintenance log (date reported, issue summary, current status). Fetch on open (fresh data, like Block 16).

**Step 17.4 — `Edit(EditVehicleViewModel)` (POST).** Update the vehicle's plate/type/route (+ `updated_at`); update the maintenance log's `maintenance_status` + `verified_by`. When the status is set to **Resolved**, stamp `maintenance_logs.resolved_at = UtcNow` **and** `vehicles.last_maintenance_date = today` — this is the prose's "update the Last Maintenance Date when a bus returns from the shop," done automatically on resolve to match the mockup (which shows no free date picker). Optionally, if the vehicle's `vehicle_status` was `flagged`, flip it back to `ready to deploy` on resolve so the registry badge clears. `[ValidateAntiForgeryToken]` + PRG + `TempData`.

**Step 17.5 — `_EditVehicleModal.cshtml`** matching the mockup: centered **"Edit Vehicle"** title + ×; **Vehicle Profile** (Vehicle ID greyed/disabled, Plate Number, Vehicle Type, Route); a **Maintenance Log Details** section bar with a current-status badge (Date Reported, Issue Summary, **Change Status** dropdown, **Verified by** input); **Cancel** + **Save Changes** (teal). Wire Block 14's **Edit** button (fetch `EditForm` on open); auto-reopen on failed POST.

**Files touched**

| Action | Path |
|---|---|
| modify | `Controllers/VehiclesController.cs` (`EditForm`, `Edit`) |
| modify | `Models/MaintenanceLogModel.cs` (add `VerifiedBy`) |
| new | `Models/EditVehicleViewModel.cs` |
| new | `Views/Vehicles/_EditVehicleModal.cshtml` |
| modify | `Views/Vehicles/Index.cshtml` (wire Edit button + fetch JS) |
| (SQL) | `ALTER TABLE maintenance_logs ADD verified_by` run in Supabase SQL Editor |

**Verify.** **Edit** changes the profile fields and persists; **Change Status** + **Verified by** update the maintenance log; setting status to **Resolved** stamps `resolved_at` + `last_maintenance_date` and flips the registry's Maintenance badge to **No Issues**; **Vehicle ID is non-editable**. **⏸ This completes the Vehicles track (all four mockups delivered).**

**Addendum — static UI done (2026-06-17).** `_EditVehicleModal.cshtml` is built to the mockup with a hardcoded **BUS-02 example** (Vehicle Profile with **Vehicle ID disabled/greyed**, Plate/Type/Route; "Maintenance Log Details" bar with a Needs-Attention badge, Date Reported, Issue Summary, **Change Status** dropdown, **Verified by** input; Cancel + Save Changes). Form `action="#"` placeholder; same static-modal caveat as Block 16 (every Edit opens the same example). **Still to do:** the `verified_by` schema column + `MaintenanceLogModel` property, `EditVehicleViewModel`, `GET EditForm(id)` fetch, `Edit` POST, and per-row wiring.

**Data-wiring done (Block 17 proper, 2026-06-17).** The Edit modal now loads + posts for real, mirroring Block 16's fetch-partial split:
- **⚠ Schema (must run before testing):** `ALTER TABLE maintenance_logs ADD COLUMN IF NOT EXISTS verified_by text;` in the Supabase SQL Editor (coordinate with Aleah). `MaintenanceLog` gained a matching `[Column("verified_by")] VerifiedBy`. Until the column exists, the `Edit` POST's full-row `.Update(log)` will 400.
- `Models/EditVehicleViewModel.cs` — `VehicleId` (read-only PK), `PlateNumber`/`VehicleType`/`RouteId` with DataAnnotations; maintenance fields `LogId` (nullable), `MaintenanceStatus`, `VerifiedBy`; display-only `DateReported`/`IssueSummary`/`CurrentStatus`/`HasMaintenance`; and its own `RouteOptions`/`TypeOptions`/`StatusOptions` so the fetched partial is self-sufficient.
- Modal split like View: `_EditVehicleModal.cshtml` is now a **shell** (`#editVehicleContent`, spinner) and the new **`_EditVehicleForm.cshtml`** (`@model EditVehicleViewModel`) holds the `<form asp-action="Edit">`. On a **failed POST** the server pre-renders the form inline via `ViewBag.EditVehicleModel` and reopens it (`ViewBag.OpenModal = "EditVehicle"`).
- `VehiclesController`: `GET EditForm(id)` → `BuildEditViewModelAsync` (loads vehicle + routes + the maintenance log it edits — **latest unresolved, else latest overall**) → `_EditVehicleForm`; `POST Edit` updates the vehicle profile (+ `updated_at`) and, if a log is being edited, its `maintenance_status` + `verified_by`. New shared helpers `BuildRouteOptions`/`BuildTypeOptions`, vocab array `MaintenanceStatusOptions`, and `ReRenderIndexForEditAsync`.
- **"Change Status" offers exactly the three `maintenance_status_enum` labels** (`Needs Attention`/`Under Repair`/`No Issues`), per the §2.8 reconciliation (the static mockup's "Resolved" is dropped). **Selecting "No Issues" is the resolve action:** stamps the log's `resolved_at`, the vehicle's `last_maintenance_date`, and flips a `Flagged` vehicle back to `Ready to Deploy` so the registry badge clears.
- `Index.cshtml` wires the Edit button (delegated click → `loadVehicleEditForm` fetch) and the `EditVehicle` auto-reopen branch. (Like the Add modal, client-side validation isn't loaded on this page — server-side `ModelState` is the backstop.)

---

## 5. Files touched (summary)

| Action | Path | Block(s) |
|---|---|---|
| modify | `Program.cs` — remove Identity/SQL Server; cookie auth; seeder; later `FareCalculator` + `TelemetrySimulator` | 0, 1, 8, 9 |
| modify | `appsettings.json` — remove `DefaultConnection`; add `FleetWise:FareRate` | 0, 8 |
| modify | `FleetWise.csproj` — drop EF/Identity/SqlServer packages (keep hasher) | 0 |
| delete | `Data/ApplicationDbContext.cs`, `Data/Migrations/*`, `Areas/Identity/*` | 0 |
| modify | `Views/Shared/_LoginPartial.cshtml` — claims-based, no Identity | 0, 1 |
| new, then deleted | `Data/DbSeeder.cs` — seeded roles/users/routes/vehicles/trips, then removed once Supabase held the data | 0 |
| (SQL) | `UPDATE routes SET waypoints_json=..., stops_json=...` for the two seed routes, run in Supabase SQL Editor alongside Block 5's `ALTER TABLE` | 5 |
| new | `Services/AuthService.cs` | 1 |
| modify | `Controllers/HomeController.cs` — real login + Logout | 1 |
| modify | operator controllers — `[Authorize]` | 1 |
| modify/new | `Controllers/UsersController.cs` + `Views/Users/Index.cshtml` | 2 (then 3,4) |
| new | `Models/ViewModels/UserListItemViewModel.cs` | 2 |
| new | `Models/ViewModels/AddUserViewModel.cs`, `EditUserViewModel.cs` + `_AddUserModal`, `_EditUserModal` | 3 |
| new | `Models/ViewModels/RoleFormViewModel.cs` + `_ManageRolesModal` | 4 |
| modify | `Models/BusRoute.cs` — add `WaypointsJson`, `StopsJson` | 5 |
| modify/new | `Controllers/FleetMapController.cs` + `Views/FleetMap/Index.cshtml` | 5 (then 6,7,10,13) |
| new | `wwwroot/js/fleetmap.js` | 5 (then 6,7,11,12,13) |
| (SQL) | `ALTER TABLE routes ADD waypoints_json, stops_json` in Supabase SQL Editor | 5 |
| new | `Models/ViewModels/StopDto.cs`, `BusPositionDto.cs` | 6, 10 |
| new | `Services/FareCalculator.cs` | 8 |
| new | `Services/TelemetrySimulator.cs` | 9 |
| modify | `Controllers/VehiclesController.cs` — `Index`/`VehicleRows`/`Create`/`Details`/`EditForm`/`Edit` | 14–17 |
| new | `Views/Vehicles/Index.cshtml`, `_VehicleRows.cshtml`, `_AddVehicleModal.cshtml`, `_VehicleDetails.cshtml`, `_EditVehicleModal.cshtml` | 14–17 |
| new | `Models/VehicleListItemViewModel.cs`, `VehiclesIndexViewModel.cs`, `AddVehicleViewModel.cs`, `VehicleDetailsViewModel.cs`, `EditVehicleViewModel.cs` | 14–17 |
| modify | `Models/MaintenanceLogModel.cs` — add `VerifiedBy` | 17 |
| (SQL) | `ALTER TABLE maintenance_logs ADD verified_by` in Supabase SQL Editor | 17 |

**Out of scope** (YAGNI / owned by teammates): module-level permission *enforcement* beyond role; password-reset email / lockout / 2FA; `messages` features; Dashboard/Reports/Dispatch pages; SignalR; the mobile app (Chester writes `bus_checklist` / telemetry — the Vehicles page only reads them); migrating to Supabase Auth.

---

## 6. Database & seeding workflow (Franz's part)

The Supabase database is **shared and cloud-hosted**, and the leader owns its schema. The division of labor:

1. **Schema already exists.** All 9 tables are live (verified). Franz writes **no** migrations for them. **Block 0 (User Management's prerequisite) makes zero schema changes** — it only seeds data, so it can land immediately without waiting on the leader.
2. **The one additive change** (Block 5, the first Fleet Map block — not Block 0): the two `routes` geometry columns. Claude provides the exact SQL; it is run **once** in the **Supabase dashboard → SQL Editor**, coordinated with Aleah (since it's the shared schema):
   ```sql
   ALTER TABLE routes ADD COLUMN IF NOT EXISTS waypoints_json text;
   ALTER TABLE routes ADD COLUMN IF NOT EXISTS stops_json    text;
   ```
3. **Seeding the empty tables** is done by `Data/DbSeeder.cs` at app startup — idempotent, so it runs safely every boot and only inserts what's missing. Block 0 fills `roles`, `users`, `routes` (no geometry), `vehicles`, `trips` for the first time; Block 5 adds `waypoints_json`/`stops_json` to the seed routes. Once Block 9 lands, `telemetry_data` fills automatically while the app runs.
4. **To verify from Supabase** (Table Editor or SQL Editor): `select * from users / roles / routes / vehicles / trips` show seeded rows; `select count(*) from telemetry_data` climbs while the app runs.

> **Security note (carry into the team chat):** the Supabase URL **and publishable key are committed** in `appsettings.json`. The publishable key is meant for client use, but it's in source control — confirm **Row-Level Security** is enabled on these tables in Supabase, or anyone with the key can read/write them. This is a real item, not a nicety, because the key is already public in the repo history.

---

## 7. Heads-up for teammates — what changes under them

Post this (or link it) when the branch merges. Most items land with **Block 0** and **Block 1**.

- **Everyone — the SQL Server / ASP.NET Identity stack is removed.** Block 0 deletes `ApplicationDbContext`, the EF Identity migration, `Areas/Identity`, the `DefaultConnection`, and the EF/Identity/SqlServer NuGet packages. The app is **Supabase-only** now. Pull `master` before branching; `Program.cs`, `appsettings.json`, and `FleetWise.csproj` changed. If your branch still references `IdentityUser`, `SignInManager`, `ApplicationDbContext`, or `UseSqlServer`, it will fail to build — switch to the Supabase models + `AuthService`.
- **Everyone — login is real now.** Operator pages carry `[Authorize]`; you must log in (a seeded account, e.g. `admin@fleetwise.com`) to reach Dashboard/Users/FleetMap/etc. Auth is **cookie-based against the Supabase `users` table** (passwords hashed with `PasswordHasher`) — no Identity. The logged-in user's **role** is a claim, so `User.IsInRole("Admin")` works if you need it.
- **Aleah (leader, Dashboard/Reports):** your Supabase data layer is the foundation this whole plan builds on — thank you. Two things to know: (1) The seeder (Block 0) fills the empty tables as soon as User Management starts, so your Dashboard will show real numbers locally; adjust the seed data freely. (2) Later, Block 5 (Fleet Map) adds `waypoints_json` / `stops_json` to `routes` via the SQL Editor — please okay/run that when it comes up; it doesn't affect your queries. For revenue math, **reuse `Services/FareCalculator`** + the `FleetWise:FareRate` key rather than hardcoding a fare, so the dashboard and the map agree.
- **Jhuztin (Dispatch):** **Vehicle Management moved to Franz on 2026-06-17** (Blocks 14–17) — Franz now owns `VehiclesController` + `Views/Vehicles`. Build your Dispatch CRUD on the **existing** Supabase models (`BusRoute`, `Trip`) — please don't create duplicates. Need more columns? Add them in the Supabase SQL Editor + the model class. Your `DispatchController` file is untouched; the seeder (Block 0) gives you sample routes/trips to develop against right away.
- **Chester (Mobile):** driver accounts created on the Users page are rows in the Supabase `users` table with a `role_id` for Driver and a hashed `password_hash`. Coordinate the mobile login flow with Franz — it should verify against the same `users` table (same `PasswordHasher` scheme). Two write contracts the Operator dashboard reads: (1) **`bus_checklist`** — your driver pre-departure inspections drive the Vehicles page's Status badge and its Inspection Log (Blocks 14, 16); (2) **`telemetry_data`** — when your app graduates from manual counting, insert rows (`trip_id, latitude, longitude, current_passengers, timestamp`). The simulator (Block 9, Fleet Map) fakes telemetry and gets switched off (one line in `Program.cs`) the day your app sends real data — the web map won't change.
- **Seed credentials:** the admin and sample drivers are created by `Data/DbSeeder.cs`; credentials are in the PR description. **Change the admin password before any public demo**, and **enable RLS** on the Supabase tables (§6 security note).

---

## 8. Risks and mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| Teammate branch breaks from the Identity/SQL Server removal | High (for any stale branch) | §7 notice; the build error (`IdentityUser` / `ApplicationDbContext` not found) is distinctive and the fix is mechanical (switch to Supabase models + `AuthService`) |
| Supabase publishable key + URL committed in the repo | **Certain (already done)** | §6/§7 flag it; **enable Row-Level Security** on the tables so the key can't be abused; treat the key as public and rely on RLS, not secrecy |
| Custom auth security (we own hashing/session) | Medium | Use `PasswordHasher` (PBKDF2), never hand-rolled crypto; cookie auth with HttpOnly/secure cookies; advanced features (lockout, reset) deferred but the table supports adding them |
| App started before Block 0's seed data exists | Medium | Seeder is idempotent and try/catch-wrapped — a Supabase hiccup logs a warning and the app still boots |
| `telemetry_data` grows in the cloud DB (1 row/bus/5 s) | Medium | Simulator is Development-only; optional one-line "delete older than N hours" in the tick if it gets noisy (deferred — YAGNI) |
| Two members edit `Program.cs` concurrently around Block 0/1 | Medium | §7 asks everyone to pull `master` first; these files settle after Block 1 |
| OSM tile server unreachable on demo network | Low | Leaflet caches tiles in-session; worst case the basemap greys but markers/panel still work |

---

## 9. Execution order and dependencies

Each block is built, reviewed, and (where it touches runnable code) verified before the next starts. **User Management ships first:** Blocks 0–4 are one front-to-back priority track; Fleet Map (Blocks 5–13) starts only once Block 4 is signed off.

1. **Block 0** (cleanup, seed data) — *blocks everything.* No later block can be verified at runtime until the dead stack is gone and the tables hold data. Makes **zero** schema changes, so it doesn't wait on the leader.
2. **Block 1** (custom auth) — comes right after 0, because every operator page now requires login and the User Management blocks are about the accounts auth uses.
3. **User Management — Blocks 2 → 3 → 4, in strict sequence:** Block 3 depends on Block 2's table; Block 4 reuses Block 3's PRG/`TempData` pattern. **Block 4 completes User Management** (Figures 24–27 all delivered).
4. **Fleet Map — Blocks 5 → {6, 7} → {8, 9} → 10 → 11 → {12, 13}**, starting after Block 4. Block 5 also applies the one schema change in this plan (§2.4/§2.5). Blocks in `{}` have no dependency on each other.
5. **Vehicles — Block 14 → {15, 16, 17}.** Block 14 (registry) first; the three modals depend only on it and are independent of one another. The track depends solely on auth (Block 1), so it can run **in parallel with the Fleet Map track** — its only schema change (`maintenance_logs.verified_by`, Block 17) is isolated and coordinated with Aleah.
6. Each block is presented for sign-off individually (track via §4.0's Status column) before the next — small diffs, fast reviews, no re-review of approved blocks.
7. §7 is posted to the team when the branch merges.

---

## 10. Verification

Each block has its own quick "Verify". This is the **full end-to-end pass**, run once a track is complete and again before merging to `master`.

1. **Build:** `dotnet build` — clean compile, **zero** SQL Server/Identity references remaining.
2. **Seed data (Block 0):** in Supabase, `users`/`roles`/`routes`/`vehicles`/`trips` hold the seeded rows — `roles` has `Admin`/`Dispatcher`/`Driver`, each with the §2.2 permission JSON (7 `web_permissions` keys incl. `Analytics`, 1 `mobile_permissions` key `FullAccess`); `routes` has no geometry yet (added in Block 5).
3. **Auth (Block 1):** `dotnet run`; logging in with a seeded account reaches the Dashboard and the navbar greets the user; wrong password shows the inline error; `/Users` while logged out redirects to login; Logout re-blocks the operator pages.
4. **Run the mockups:**
   - **/Users (Blocks 2–3; Figures 24, 26, 27):** table matches Figure 24 exactly — Name, Email, Role, Status, Actions (Edit only), no Last Login column; Roles dropdown filters; search matches partial name/email; **Add User** auto-fills a generated read-only Initial Password on modal open, then creates an account that appears immediately **and can log in** with that password; duplicate email re-opens the modal with the error and input preserved; **Edit** changes fields and only re-hashes the password when one is typed; switching **Account Status to Deactivated** in Edit flips the badge to grey and immediately blocks that account's login — reactivating restores both.
   - **Manage Roles (Block 4; Figure 25):** left list shows `Admin`/`Dispatcher`/`Driver`; **"+ Add Role"** opens a blank panel (Role Name, Access Level, all toggles off); saving a new role with some toggles on persists, survives reload, and appears in the Block 2 filter and Block 3 Add/Edit dropdowns; toggles round-trip exactly — 7 keys in `web_permissions`, 1 key (`FullAccess`) in `mobile_permissions` (`select role_name, web_permissions, mobile_permissions from roles`).
   - **Schema + geometry (Block 5):** in Supabase, `routes` now has `waypoints_json`/`stops_json` populated for both seed routes.
   - **/FleetMap (Blocks 5–13; Figures 17–19):** map renders centered on the seeded routes with stop markers; bus markers visibly move every ~5 s, icons pointing along travel; **hover** shows the tooltip (plate, route, passengers); **click** opens the side panel (driver, Bus ID, route, status, occupancy bar, revenue) with live-ticking numbers; Route/Status filters narrow the markers; stopping the app stops movement (data comes from the DB).
   - **Telemetry contract (Blocks 9–10):** in Supabase, `select * from telemetry_data order by timestamp desc limit 20` shows fresh rows; `occupancyPct` equals `passengers/capacity`; `estimatedRevenue` equals `passengers × FleetWise:FareRate`.
   - **/Vehicles (Blocks 14–17; the Vehicles mockups):** registry matches the mockup — three summary cards (Total / Flagged / Scheduled Maintenance) with correct counts, **Route / Type / Status / Issues** filters + search, and a **Vehicle Information** table with **Status** (Ready to Deploy / Pending / Flagged) and **Maintenance** (No Issues / Needs Attention / Under Repair) badges colored per mockup; **+ Add Vehicle** creates a unit (duplicate Vehicle ID rejected); **View** opens the read-only details modal (Vehicle Profile + Inspection Log with the yellow Remarks box + Maintenance Log timeline); **Edit** updates the profile and the maintenance log (Change Status / Verified by), and resolving stamps `last_maintenance_date` and flips the badge to No Issues; Vehicle ID is non-editable. Confirm in Supabase: `maintenance_logs` has the `verified_by` column.
5. **Regression sanity:** app boots with zero DI errors; Dashboard shows real seeded numbers; Dispatch/Reports placeholders still render (behind login); sidebar navigation and active-state highlighting still work.
