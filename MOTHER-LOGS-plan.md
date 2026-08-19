# Mother Logs — suite-wide security & audit log (the third A)

> Status: GRILLED + LOCKED (2026-07-13). Local doc, NOT committed (like PHASE7 /
> REMOTE-CONTROL). Depends on Phase 7 auth (roles, jwt helpers) + Phase 8 tables. Fills
> the Accounting/Auditing leg of AAA — Authentication + Authorization already strong (Phase 7).
> UI name = "Audit Log"; "Mother Logs" is the internal codename.

## Locked decisions (grilled 2026-07-13 — do not relitigate)

| # | Decision | Choice |
|---|----------|--------|
| 1 | Actor attribution | **Split by surface.** DB triggers own mobile actions (real per-user/device JWT in the request); web admin actions logged in-controller from the ASP.NET cookie (`User.FindFirst(NameIdentifier)`) — the shared service key means the DB can't see which admin acted; auth events at the edge fns. |
| 2 | Trigger scope | **Crown jewels only: `users` + `device_config`.** Everything else via web-server + edge logging. Triggers earn their complexity only where a forged mobile write is the scary case (identity + the Phase-8 remote-camera surface). |
| 3 | Detail depth | **Old/new JSON diffs on the two trigger tables** (column DENYLIST — never `password_hash`/tokens/secrets) + human summary lines for web actions. |
| 4 | Denials | **Log denials only where we control the response** — edge (`login_failed`, `token_refused`) + web (permission/action rejections). No DB-level RLS-42501 scrape. |
| 5 | Viewer | New **`audit` permission**, granted to the **top admin role only** by default (reassignable via Manage Roles); own top-level **"Audit Log"** sidebar page, filter + paginate. |
| 6 | Retention | **Never auto-prune.** Excluded from `TelemetryRetentionService`. Low volume (heartbeats/telemetry are out of scope), and a complete immutable trail is the point. |
| 7 | Failed-login lockout | **Persist-only now.** `login_failed` rows flow so attack patterns are visible; durable account lockout deferred to a clean 10d, pulled in only when needed. |

## 0. Problem

The suite can prove WHO you are (auth) and STOP what you can't do (RLS), but keeps almost
no record of WHAT HAPPENED. Only `users.last_login` + the maintenance incident notes exist.
No trail of: logins (success/fail), token mints, admin actions (create user, reset password,
out-of-service, schedule edits, broadcast messages), or remote camera calibration. For a
security story — and for a capstone defense — that gap is the weak point. "Mother Logs" =
one append-only audit trail every authoritative surface writes to.

## 1. Core design principle: log at SERVER-AUTHORITATIVE points only

A log a client controls is a log a compromised client can forge. So the actor identity is
NEVER taken from the client's word — it's read server-side. But the web and mobile see
different truths, so attribution is SPLIT BY SURFACE (locked #1):

**Why the split:** the web dashboard talks to Supabase through ONE shared service key
(`Program.cs` singleton), so every web DB write hits Postgres as `service_role` — the DB
literally cannot tell which admin did it. The admin's identity lives only in the ASP.NET
Identity cookie. Mobile, by contrast, carries a real per-user/device JWT
(`app_driver`/`app_camera`) on every request, which the DB CAN read. So:

1. **DB triggers** — on `users` + `device_config` ONLY (locked #2). `SECURITY DEFINER`,
   read `request.jwt.claims` for the real mobile actor (driver user_id / camera device_id).
   Tamper-proof: a forged driver/camera write still trips the trigger. A web action here
   logs actor=`service_role` (attribution comes from the matching web-server row instead).
2. **Edge functions** — auth events the DB can't see: `login` ok, `login_failed`,
   `token_mint`, `token_refused`, `change_password`. Written with the service key.
3. **Web server** — the ONLY place that knows which admin acted (cookie). Every mutating
   admin action logs a row: actor = the admin's user_id, human `summary`, target. This is
   PRIMARY for web, not belt-and-suspenders.

Mobile apps write NOTHING to the log directly — their audit-relevant actions surface through
the two DB triggers, so a tampered APK can't poison the trail with a forged actor.

## 2. Schema

```sql
create table audit_log (
  id           bigint generated always as identity primary key,
  occurred_at  timestamptz not null default now(),
  actor_type   text not null,          -- 'user' | 'device' | 'admin' | 'system' | 'anon'
  actor_id     text,                    -- user_id | device_id | null
  actor_role   text,                    -- app_driver | app_camera | admin role | null
  action       text not null,          -- 'login' | 'login_failed' | 'token_mint' |
                                        -- 'user_create' | 'password_reset' | 'vehicle_oos' |
                                        -- 'trip_update' | 'camera_calibrate' | 'insert' |
                                        -- 'update' | 'delete' | ...
  target_table text,                    -- 'users' | 'vehicles' | 'trips' | 'device_config' ...
  target_id    text,                    -- PK of the affected row
  source       text not null,          -- 'web' | 'driver' | 'camera' | 'edge' | 'db'
  outcome      text not null default 'ok', -- 'ok' | 'denied' | 'error'
  summary      text,                    -- human line: "Admin reset password for J. Cruz"
  changes      jsonb,                   -- {old:{...}, new:{...}} diff (sensitive tables only)
  ip           text,                    -- edge/web only; null from DB triggers
  request_id   text                     -- correlate one action's multiple log rows
);
create index on audit_log (occurred_at desc);
create index on audit_log (actor_id, occurred_at desc);
create index on audit_log (target_table, target_id);
```

## 3. Tamper resistance (append-only)

- RLS on. **No UPDATE, no DELETE for anyone** — not app roles, not `authenticated`, not even
  the service role via the API (revoke; only a manual DBA SQL session could touch it).
- INSERT only through the `SECURITY DEFINER` trigger fn (DB path) or the service key
  (edge/web path). App roles get zero direct grants.
- Admin SELECT via a definer view / web endpoint gated by a new `audit` permission.
- Result: the log can be read and appended, never rewritten or trimmed from inside the app.

## 4. What gets logged (scope — "everything" that matters, not literal every row)

Writer in brackets: [T]=DB trigger, [E]=edge fn, [W]=web server.

| Surface | Events | Writer |
|---|---|---|
| Auth | login ok, login_failed (bad-email vs bad-pwd distinguished internally, same 401 out), token_mint, token_refused, change_password | [E] |
| users | create, role change, status change, profile edit, password_reset, delete — with denylisted old/new diff | [T] + [W] intent line |
| device_config | remote calibration writes (who moved the line: driver:N / admin / device), version, line/lens diff | [T] + [W] for admin (8e) |
| vehicles | out_of_service on/off, status forced, counter bind/unbind | [W] |
| trips | create, reassign, status change (start/complete/cancel), delete | [W] |
| schedule | cell add/edit/delete, conflict-override save | [W] |
| messages | broadcast / route / driver message sent | [W] |
| authz denials | permission/action rejections | [E] + [W] only |

**Explicitly NOT logged** (noise + cost): count heartbeats, telemetry GPS rows, device_status
last_seen pings, ordinary reads, DB-level RLS 42501s (write never lands — not catchable
without a log scrape, locked #4). Auditing every 5s heartbeat would bury the signal.

**Diff denylist (locked #3):** the trigger's old/new JSON NEVER includes `password_hash` or
any token/secret column. The audit table must never become a credential store.

## 5. Build order

1. **10a — table + the two triggers + auth logging.** `audit_log` (RLS append-only, §3),
   generic `SECURITY DEFINER` trigger fn (jwt actor + denylisted old/new diff) attached to
   `users` + `device_config` ONLY. Edge fns (`auth-login`, `device-token`, `change-password`)
   write auth rows via service key. Prove: a driver/camera action on those tables → one
   honest trigger row with the right actor; a login attempt → an edge row.
   **STATUS: CODE DONE (2026-08-17).** `supabase/phase10a.sql` (table + 4 indexes;
   append-only lockdown = revoke all from app roles + revoke UPDATE/DELETE/TRUNCATE from
   service_role, RLS on with NO policies; `audit_row_change()` SECURITY DEFINER reading
   `request.jwt.claims`, `- 'password_hash'` denylist on both old and new; triggers on
   users ins/del + users update WITH a WHEN that ignores last_login/updated_at-only
   changes so driver logins don't double-log; separate `audit_password_change()` slim row
   for hash changes since the main WHEN filters them out; device_config ins/del + update
   `when (old.* is distinct from new.*)` to skip no-op PATCHes).
   `supabase/functions/_shared/audit.ts` = service-key writer, awaited but fully
   error-swallowed (audit must never lock anyone out), IP from x-forwarded-for.
   Wired: auth-login (login / login_failed with INTERNAL reason while the client still
   gets one generic 401 / rate-limit block), device-token (token_mint / token_refused),
   change-password (ok + denied).
   **VERIFIED LIVE (2026-08-17):** edge rows land (`login_failed` w/ internal reason +
   client still gets the generic 401; `login` w/ app_driver actor; IP captured), DB
   trigger rows land w/ correct actor (a camera self-write logged as
   `device cam-…`, an admin write as service_role). GOTCHAS: (1) the three fns must be
   DEPLOYED (`npx supabase@latest functions deploy <fn> --project-ref vrtluruqaxutecydbrsq
   --no-verify-jwt`) — no global supabase CLI on this PC and the link is stale, so npx +
   explicit --project-ref; running the SQL alone leaves zero `source=edge` rows.
   (2) First cut logged every web "refresh photo" because the web calibrator PATCHes
   `wake_requested_at` on device_config as a doorbell -> trigger WHEN now ignores
   wake_requested_at/updated_at-only writes, so web matches mobile (log on Save only).
   (3) device_config summaries are purpose-built ("counting line moved (v93, by device)")
   — "Update on device_config" told an auditor nothing.
   (4) **BIGGEST ONE — privileges alone did NOT make the table append-only.** A DELETE
   from the dashboard SQL editor SUCCEEDED (it connects as `postgres`; owners and
   superusers bypass GRANT/REVOKE and RLS entirely). Fixed with BEFORE UPDATE/DELETE row
   triggers + a BEFORE TRUNCATE statement trigger raising P0001 — triggers fire regardless
   of privilege level. Re-verified: DELETE now errors even as postgres. Not unbreakable
   (a superuser can DROP the guard trigger first) and nothing in Postgres can be; the
   standard achieved is "erasing history takes a deliberate, visible act, not a stray
   DELETE". Row id 1 was lost proving this.
2. **10b — web-server logging.** A small `AuditLog` service (service-key insert) called from
   every mutating admin controller action (UsersController, DispatchController, Vehicles
   controller oos/bind, ScheduleController, message send, CameraSave 8e). Actor = cookie
   user_id, human `summary` + target. This is where web attribution lives (§1).
   **STATUS: CODE DONE (2026-08-17), NOT YET VERIFIED LIVE.**
   `RouteSyncWeb/Services/AuditLog.cs` — scoped, needs `AddHttpContextAccessor()`; raw
   PostgREST POST to `rest/v1/audit_log` with the service key (same pattern as
   VehiclesController's CamReq, avoids needing a Postgrest model for an identity PK);
   `Prefer: return=minimal`; 3s CancellationTokenSource + try/catch swallow so a slow
   audit table can never hang or break an admin click. Two entry points: `WriteAsync`
   (signed-in admin, actor from the cookie claims, prepends the operator's display name to
   the phrase so every line reads the same) and `WriteSignInAsync` (login/logout, where
   there is no principal to read yet — a failed attempt has no identity and a successful
   one is signed in on the RESPONSE, so the caller passes what auth just established).
   `actor_role` on web rows = the DASHBOARD role (Admin/Dispatcher), not a Postgres role;
   `source` disambiguates.
   Wired: Home (login / login_failed w/ the typed email length-capped / logout /
   self-service change_password), Users (user_created, user_updated w/ status+role delta,
   password_reset, role_created, role_updated w/ the granted section list), Vehicles
   (vehicle_created, vehicle_updated, incident_resolved, vehicle_grounded,
   vehicle_returned, maintenance_scheduled, camera_calibrated on Save only), Dispatch
   (trip_created, trip_reassigned w/ from→to, trip_removed, message_sent w/ SUBJECT only,
   driver_availability; override flags called out in the summary), Schedule (schedule_saved
   w/ added/changed/removed counts, skipped entirely when nothing changed).
   NOT wired on purpose: `AddNote` (maintenance_notes already stores author_id +
   author_name — it is its own audit thread), camera wake/snapshot (per-frame doorbell,
   same reason the DB trigger ignores it). Camera bind/unbind is NOT a web surface (it
   happens in the camera app), so there is nothing to log there.
   Web rows deliberately DOUBLE UP with the DB trigger rows on users/device_config: the
   trigger row says WHAT changed (diff), the web row says WHO and from which IP.
3. **10c — Audit Log viewer.** New `audit` permission (top admin only by default), own
   sidebar page. Filter by actor / action / target / date; paginate; newest first; summary
   line + expandable `changes` diff.
   **STATUS: CODE DONE (2026-08-17), NOT YET VERIFIED LIVE.**
   `AuditController` (read-only, `[RequirePermission("audit")]`), `Views/Audit/Index.cshtml`,
   `Models/AuditViewModels.cs`, `AuditLog.QueryAsync()` (raw PostgREST GET, `Prefer:
   count=exact`, total parsed out of the `Content-Range` header), `PhClock.ToPh()` for
   display. `supabase/phase10c.sql` grants `audit` to the admin role and writes an explicit
   false everywhere else. Sidebar link + the `audit` key added to `WebPermissionKeys`,
   `_ManageRolesModal`, and the Users page JS.
   GOTCHAS BUILT AROUND: (1) the filter param is `type`, NOT `action` — MVC's default
   route is `{controller}/{action}/{id?}`, so a parameter named `action` binds to the route
   value "Index" and the filter silently never works. (2) The search term is stripped of
   `,()*"\` rather than escaped, because it is interpolated inside PostgREST's `or=(...)`
   list where those characters are structure. (3) `occurred_at` is real UTC (Postgres
   `now()`), NOT the PH-wall-clock convention the rest of the suite writes, so date filters
   send explicit `+08:00` boundaries and display converts. (4) A failed READ renders "could
   not load", never an empty table — an empty audit page reads as "nothing ever happened".
   (5) Permission claims are stamped at LOGIN, so granting `audit` needs a sign out and
   back in before the page appears.
   DECIDED AGAINST: logging the act of viewing the audit log. It is a real security idea
   (log access to the logs) but every filter change and page click would write a row, which
   is the exact noise problem the device_config doorbell caused. Revisit only if the trail
   is ever exposed beyond the top admin.
4. **10d — DEFERRED: durable failed-login lockout.** Not in the initial build (locked #7).
   Once `login_failed` rows flow, add: count recent failures per email → refuse inside a
   lock window (+ auto-unlock or admin-unlock path). Pull in only when wanted.

Retention: never-prune is a property of the table (no DELETE grant, excluded from the
retention sweep), set in 10a — not a separate phase.

## 6. AAA after Mother Logs

- **Authentication** — unchanged strong; now failed attempts are RECORDED (enables real
  lockout, §10d).
- **Authorization** — unchanged strong; denials become visible (§7).
- **Accounting** — from near-zero to a tamper-resistant, server-authoritative, queryable
  trail of every security-relevant action across all three clients. Third A closed.

## 7. Settled (was open; resolved in the grill)

All resolved into the Locked-decisions table at the top. For the record:
- Diff depth → diffs on the two trigger tables, `password_hash`/secrets denylisted (#3).
- RLS denials → edge + web only, no DB scrape (#4).
- Viewer → `audit` perm, top admin only, own page (#5).
- Read auditing → NOT logged (ordinary reads are noise; revisit only if a
  sensitive-export feature is added).
- Name → "Audit Log" UI, "Mother Logs" codename.
- Failed-login lockout → deferred to 10d (#7).
