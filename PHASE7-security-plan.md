# Phase 7 — RouteSync Security Hardening (RLS + Auth)

> Status: **DONE — 7a-7d all cut over + verified 2026-07-10.** Anon fully revoked; both
> apps JWT-only; web on secret key (gitignored appsettings.Secret.json, see .example +
> RouteSyncWeb/README.md). Deviations from plan: users_app keeps role_id; account status
> string is 'Activated'; table is bus_checklist (singular); JWT_SECRET = dashboard
> "Legacy JWT Secret" (project signs its own tokens with ECC now — do NOT revoke the
> legacy HS256 key or every app JWT dies). Local planning doc, NOT committed.

## 0. Threat model (what today's setup allows)

One publishable key `sb_publishable_sjkjW2K7QOPRKmixJdhSgA_8rPtoFzD` is embedded in the
driver APK, camera APK, and web appsettings. PostgREST tables have no RLS. Therefore
anyone holding the key (extractable from any APK) can, from anywhere:

- **PATCH `users.password_hash`** → take over any account (incl. admins).
- **SELECT `users.password_hash`** → dump all hashes, crack offline. (Driver app verifies
  passwords CLIENT-side — the hash crossing the wire is by design today.)
- Write `trips`, `vehicles`, `telemetry_data`, `messages`, … → corrupt operational data.

## 1. Locked decisions (do not relitigate)

| # | Decision | Choice |
|---|----------|--------|
| 1 | Auth architecture | **Edge-function-minted custom JWTs**, keep homemade `users` table. No Supabase Auth migration (hash format incompatible → would force fleet-wide password resets). |
| 2 | Endpoint home | **Supabase Edge Functions** (Deno). Not the ASP.NET web server — phones must not depend on localhost-hosted web app. PBKDF2 (ASP.NET Identity v3 format) verified in Deno via `crypto.subtle`; format is self-describing (prf/iterations/salt embedded in the blob). |
| 3 | Camera device provisioning | **Fleet bind secret** verified server-side (edge-fn env var `FLEET_BIND_SECRET`; never in DB, never in APK). Bind UX unchanged — today's passcode becomes real. JWT claim carries `device_id` ONLY; vehicle scope enforced by DB join → rebind needs no new token. |
| 4 | Token lifetime / revocation | **Long tokens + DB-join revocation.** Camera 365d, driver 30d (monthly re-login; remember-me covers the rest). Revocation: unbind vehicle (kills camera token's power), `users.account_status != 'Active'` (kills driver token). Nuclear: rotate project JWT secret. NO refresh-token infra. |
| 5 | Anon surface | **Zero.** No reads, no writes. Both apps authenticated from first screen; login itself needs no DB reads (edge fn does the lookup). Publishable key alone = useless. |
| 6 | Hash exposure | **`users_app` view** (definer, no `password_hash`/`role` columns, built-in `WHERE user_id = jwt` + CHECK OPTION). Driver app model retargets `users` → `users_app`. Base `users` = service/edge-only. |
| 7 | Rollout | **Live Supabase project** (demo data, single dev), per-table rollback = `ALTER TABLE x DISABLE ROW LEVEL SECURITY`. Build Phase 7 NOW; accuracy field run happens after, on the final build (validates counting + security in one session). |

## 2. Roles & JWTs

Supabase maps the JWT `role` claim to a Postgres role. New roles (NOLOGIN, granted to
`authenticator`):

- **`app_driver`** — JWT `{ role:"app_driver", user_id:<int>, exp:+30d }`
- **`app_camera`** — JWT `{ role:"app_camera", device_id:"cam-xxxxxxxx", exp:+365d }`

Signed HS256 with the project JWT secret (edge fns read it from env). Clients keep sending
the publishable key as the `apikey` header (PostgREST requires it) + `Authorization:
Bearer <jwt>` for actual power.

## 3. Edge functions (Deno/TypeScript, service-role client inside)

1. **`auth-login`** — body `{ email, password }`. Loads user row (service client),
   verifies ASP.NET Identity v3 PBKDF2 hash (`0x01 | prf | iters | saltLen | salt |
   subkey`, base64) via `crypto.subtle.deriveBits`, checks `account_status='Active'`,
   updates `last_login`, returns `{ jwt, user: {…no hash…} }`. Wrong creds → 401 (same
   message for unknown email vs bad password). Basic rate limit: 5 fails/15min per email
   (in-memory or table).
2. **`device-token`** — body `{ device_id, fleet_secret }`. Constant-time compare vs
   `FLEET_BIND_SECRET` env → mints `app_camera` JWT. 401 otherwise.
3. **`change-password`** — body `{ jwt-authed, old_password, new_password }`. Verifies
   old vs stored hash, hashes new SERVER-side (same v3 format so web login stays
   compatible), updates row. Removes the last client-side hashing site.

## 4. Policy matrix (per table × role; web uses service key = bypasses RLS)

`jwt_uid` ≔ `(auth.jwt()->>'user_id')::int` · `jwt_dev` ≔ `auth.jwt()->>'device_id'` ·
`my_vehicle` ≔ `(SELECT vehicle_id FROM vehicles WHERE counter_device_id = jwt_dev)`

| Table | app_driver | app_camera |
|---|---|---|
| `users` (base) | — none — | — none — |
| `users_app` (view) | SELECT own row; UPDATE(contact_number, address, emergency_*, last_login) own row | — |
| `trips` | SELECT `driver_id=jwt_uid`; UPDATE(trip_status, actual_start_time, actual_end_time, total_boarded, estimated_revenue) same rows + `account_status` check | SELECT `vehicle_id = my_vehicle`; UPDATE(total_boarded, count_heartbeat, counter_device_id) USING `((vehicle_id = my_vehicle AND trip_status='Active') OR counter_device_id = jwt_dev)` — covers claim, 30s-stale steal, and the post-trip raise-only reconcile |
| `vehicles` | SELECT all; UPDATE(vehicle_status) rows where EXISTS(trip driver=jwt_uid, vehicle=this) | SELECT all; UPDATE(counter_device_id) USING `(counter_device_id IS NULL OR counter_device_id = jwt_dev)` WITH CHECK `(counter_device_id = jwt_dev OR counter_device_id IS NULL)` — atomic bind claim + release |
| `routes`, `fare_config` | SELECT all | SELECT (routes not needed; harmless) |
| `messages` | SELECT audience-scoped (`all` / `route` ∈ my routes / `driver`=me); UPDATE(is_read) own direct msgs | — |
| `driver_availability` | SELECT/INSERT/UPDATE own row | — |
| `bus_checklists` | INSERT + SELECT rows of own trips | — |
| `maintenance_logs` | INSERT | — |
| `telemetry_data` | — (verify during build whether TelemetryQueue writes it; add scoped INSERT if so) | — |
| ALL tables | anon: **REVOKE ALL** | anon: **REVOKE ALL** |

## 5. Client diffs

- **Driver app (MAUI):** login → call `auth-login`, store JWT (SecureStorage, replaces
  raw remember-me uid); supabase-csharp reads → `client.Auth`/`SetAuth(jwt)`; raw
  `PatchAsync/PostAsync` (HttpClient) → add Bearer header; `UserModel` `[Table("users")]`
  → `[Table("users_app")]`; password change → call `change-password` (delete local
  PasswordHasher usage); token-expiry (401/PGRST301) → bounce to login screen.
- **Camera app (Kotlin):** bind flow → call `device-token` with vehicle + fleet secret
  (the existing passcode field), store JWT in DataStore; `SupabaseApi.supabaseHeaders()`
  → `Authorization: Bearer <device jwt>`; 401 handling → "Device token invalid — re-bind"
  state. Unbind keeps local passcode gate (UX) — real security is the DB join.
- **Web (Blazor Server):** `appsettings.json` key → secret/service-role key. One line.
  (Server-side only — never shipped to browsers.)

## 6. Cutover (each step shippable, each step one-line rollback)

- **7a — Build all, enforce nothing.** Edge fns deployed, roles + view + policies created,
  RLS OFF everywhere. Both apps updated to JWT-with-anon-fallback. Everything still works
  on anon. *Rollback: n/a.*
- **7b — Flip `users`.** Enable RLS on `users`, revoke anon+roles from base, grant view.
  Hash hole dies first. *Rollback: `ALTER TABLE users DISABLE ROW LEVEL SECURITY;` + re-grant.*
- **7c — Flip the rest** (`trips`, `vehicles`, `driver_availability`, `messages`,
  `bus_checklists`, `maintenance_logs`, `telemetry_data`). *Rollback per table, same line.*
- **7d — Kill anon.** `REVOKE ALL ... FROM anon` on every table; web to secret key; remove
  anon fallback from both apps. *Rollback: re-grant SELECT/UPDATE to anon per table.*

## 7. Acceptance (curl matrix — all must hold after 7d)

1. anon `PATCH /trips` → 0 rows / 401. 2. anon `SELECT /users` → denied. 3. anon
`SELECT /users_app` → denied (no grant). 4. driver JWT `PATCH users_app` other user_id →
0 rows. 5. driver JWT response for own row contains **no `password_hash` field**.
6. camera JWT `PATCH trips` of a NON-bound vehicle → 0 rows. 7. camera JWT can PATCH only
count columns: attempt `trip_status` → column privilege error. 8. camera JWT after admin
clears `vehicles.counter_device_id` → all writes 0 rows (live revocation). 9. `auth-login`
wrong password → 401; right password → JWT that passes 4-8. 10. Whole-suite smoke: web
dashboard CRUD, driver full trip cycle, camera count+heartbeat+reconcile all green.

## 8. Effort

7a ≈ 1 day (edge fns + policies + both client updates). 7b-7d ≈ half day combined incl.
acceptance matrix. Buffer for WebView/HttpClient auth-header quirks: half day.
