-- ============================================================================
-- RouteSync Phase 7 — roles, users_app view, RLS policies (see PHASE7-security-plan.md)
--
-- 7a (THIS SCRIPT, safe to run now): creates roles/helpers/view/grants/policies.
--     RLS stays OFF — nothing is enforced, anon keeps working exactly as today.
-- 7b/7c/7d: the ENABLE ROW LEVEL SECURITY / REVOKE blocks at the bottom are
--     COMMENTED OUT. Run them per-cutover-step only.
--
-- Rollback (any table): ALTER TABLE x DISABLE ROW LEVEL SECURITY;
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Roles (NOLOGIN, assumed by PostgREST via the JWT `role` claim)
-- ---------------------------------------------------------------------------
do $$ begin
  if not exists (select 1 from pg_roles where rolname = 'app_driver') then
    create role app_driver nologin;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'app_camera') then
    create role app_camera nologin;
  end if;
end $$;

grant app_driver to authenticator;
grant app_camera to authenticator;

grant usage on schema public to app_driver, app_camera;
-- serial PKs on inserted tables (bus_checklists, maintenance_logs, telemetry_data, driver_availability)
grant usage, select on all sequences in schema public to app_driver;

-- ---------------------------------------------------------------------------
-- 2. JWT claim helpers
-- ---------------------------------------------------------------------------
create or replace function public.jwt_uid() returns int
language sql stable as $$
  select nullif(nullif(current_setting('request.jwt.claims', true), '')::json ->> 'user_id', '')::int
$$;

create or replace function public.jwt_dev() returns text
language sql stable as $$
  select nullif(current_setting('request.jwt.claims', true), '')::json ->> 'device_id'
$$;

-- Live revocation for drivers: deactivating the account kills every token instantly.
-- SECURITY DEFINER so policies can consult `users` without granting the roles any
-- access to the base table.
create or replace function public.driver_is_active() returns boolean
language sql stable security definer set search_path = public as $$
  select exists (
    select 1 from users
    where user_id = public.jwt_uid() and account_status = 'Activated'
  )
$$;

-- Camera vehicle scope: which bus is this device bound to right now?
-- Rebind/unbind changes the answer with NO new token (revocation = clear the column).
create or replace function public.camera_vehicle() returns text
language sql stable security definer set search_path = public as $$
  select vehicle_id from vehicles where counter_device_id = public.jwt_dev() limit 1
$$;

grant execute on function public.jwt_uid(), public.jwt_dev(),
  public.driver_is_active(), public.camera_vehicle()
  to app_driver, app_camera, anon;

-- ---------------------------------------------------------------------------
-- 3. users_app view — the ONLY user surface clients get.
--    * hides password_hash (the whole point)
--    * keeps role_id (harmless; the driver app model maps it — escalation is
--      prevented by column grants, not by hiding)
--    * definer view: keeps working after RLS flips on base `users` (7b)
--    * row scope: a JWT sees ONLY its own row; no JWT (anon, during migration
--      only) sees all rows — anon loses the grant entirely at 7d.
-- ---------------------------------------------------------------------------
create or replace view public.users_app as
  select user_id, first_name, middle_name, last_name, email_address, role_id,
         account_status, contact_number, address,
         emergency_contact_name, emergency_contact_number,
         created_at, updated_at, last_login
  from public.users
  where user_id = coalesce(public.jwt_uid(), user_id)
  with cascaded check option;

grant select on public.users_app to app_driver;
grant update (contact_number, address, emergency_contact_name,
              emergency_contact_number, last_login, updated_at)
  on public.users_app to app_driver;

-- Migration-window anon access (driver app fallback path). 7d: REVOKE these.
grant select on public.users_app to anon;
grant update (contact_number, address, emergency_contact_name,
              emergency_contact_number, last_login, updated_at)
  on public.users_app to anon;

-- ---------------------------------------------------------------------------
-- 4. Table grants (column-scoped where writes must be narrow)
-- ---------------------------------------------------------------------------

-- trips
grant select on public.trips to app_driver, app_camera;
grant update (trip_status, actual_start_time, actual_end_time,
              total_boarded, estimated_revenue)
  on public.trips to app_driver;
grant update (total_boarded, count_heartbeat, counter_device_id)
  on public.trips to app_camera;

-- vehicles
grant select on public.vehicles to app_driver, app_camera;
grant update (vehicle_status, updated_at) on public.vehicles to app_driver;
grant update (counter_device_id) on public.vehicles to app_camera;

-- routes / fare_config (reference data)
grant select on public.routes to app_driver;
grant select on public.fare_config to app_driver;

-- messages
grant select on public.messages to app_driver;
grant update (is_read) on public.messages to app_driver;

-- driver_availability
grant select, insert on public.driver_availability to app_driver;
grant update (availability_status, reason, updated_at)
  on public.driver_availability to app_driver;

-- bus_checklist (insert returns the row -> needs select too)
grant select, insert on public.bus_checklist to app_driver;

-- maintenance_logs / telemetry_data (write-only from the app)
grant insert on public.maintenance_logs to app_driver;
grant insert on public.telemetry_data to app_driver;

-- ---------------------------------------------------------------------------
-- 5. Policies (created now, INERT until RLS is enabled per table in 7b/7c)
-- ---------------------------------------------------------------------------

-- ===== trips =====
drop policy if exists p_trips_driver_select on public.trips;
create policy p_trips_driver_select on public.trips
  for select to app_driver
  using (driver_id = public.jwt_uid());

drop policy if exists p_trips_driver_update on public.trips;
create policy p_trips_driver_update on public.trips
  for update to app_driver
  using (driver_id = public.jwt_uid() and public.driver_is_active())
  with check (driver_id = public.jwt_uid());

drop policy if exists p_trips_camera_select on public.trips;
create policy p_trips_camera_select on public.trips
  for select to app_camera
  using (vehicle_id = public.camera_vehicle()
         or counter_device_id = public.jwt_dev());

-- USING covers: fresh claim + 30s-stale steal (Active trip on my bound bus) and
-- flush/post-trip raise-only reconcile (rows I already own).
-- WITH CHECK pins counter_device_id to me — a camera can never stamp another device.
drop policy if exists p_trips_camera_update on public.trips;
create policy p_trips_camera_update on public.trips
  for update to app_camera
  using ((vehicle_id = public.camera_vehicle() and trip_status = 'Active')
         or counter_device_id = public.jwt_dev())
  with check (counter_device_id = public.jwt_dev());

-- ===== vehicles =====
drop policy if exists p_vehicles_driver_select on public.vehicles;
create policy p_vehicles_driver_select on public.vehicles
  for select to app_driver using (true);

drop policy if exists p_vehicles_driver_update on public.vehicles;
create policy p_vehicles_driver_update on public.vehicles
  for update to app_driver
  using (exists (select 1 from public.trips t
                 where t.vehicle_id = vehicles.vehicle_id
                   and t.driver_id = public.jwt_uid()));

drop policy if exists p_vehicles_camera_select on public.vehicles;
create policy p_vehicles_camera_select on public.vehicles
  for select to app_camera using (true);

-- Atomic bind claim: take a free bus or touch my own; release sets NULL.
drop policy if exists p_vehicles_camera_update on public.vehicles;
create policy p_vehicles_camera_update on public.vehicles
  for update to app_camera
  using (counter_device_id is null or counter_device_id = public.jwt_dev())
  with check (counter_device_id = public.jwt_dev() or counter_device_id is null);

-- ===== routes / fare_config =====
drop policy if exists p_routes_driver_select on public.routes;
create policy p_routes_driver_select on public.routes
  for select to app_driver using (true);

drop policy if exists p_fare_driver_select on public.fare_config;
create policy p_fare_driver_select on public.fare_config
  for select to app_driver using (true);

-- ===== messages =====
drop policy if exists p_messages_driver_select on public.messages;
create policy p_messages_driver_select on public.messages
  for select to app_driver
  using (
    lower(coalesce(target_audience::text, '')) = 'all'
    or (lower(target_audience::text) = 'driver' and target_id = public.jwt_uid()::text)
    or (lower(target_audience::text) = 'route' and target_id in
          (select t.route_id::text from public.trips t where t.driver_id = public.jwt_uid()))
  );

drop policy if exists p_messages_driver_update on public.messages;
create policy p_messages_driver_update on public.messages
  for update to app_driver
  using (lower(target_audience::text) = 'driver' and target_id = public.jwt_uid()::text);

-- ===== driver_availability =====
drop policy if exists p_avail_driver_all on public.driver_availability;
create policy p_avail_driver_all on public.driver_availability
  for all to app_driver
  using (user_id = public.jwt_uid())
  with check (user_id = public.jwt_uid());

-- ===== bus_checklist =====
drop policy if exists p_checklists_driver_select on public.bus_checklist;
create policy p_checklists_driver_select on public.bus_checklist
  for select to app_driver
  using (exists (select 1 from public.trips t
                 where t.trip_id = bus_checklist.trip_id
                   and t.driver_id = public.jwt_uid()));

drop policy if exists p_checklists_driver_insert on public.bus_checklist;
create policy p_checklists_driver_insert on public.bus_checklist
  for insert to app_driver
  with check (exists (select 1 from public.trips t
                      where t.trip_id = bus_checklist.trip_id
                        and t.driver_id = public.jwt_uid()));

-- ===== maintenance_logs =====
drop policy if exists p_maintenance_driver_insert on public.maintenance_logs;
create policy p_maintenance_driver_insert on public.maintenance_logs
  for insert to app_driver
  with check (exists (select 1 from public.trips t
                      where t.trip_id = maintenance_logs.trip_id
                        and t.driver_id = public.jwt_uid()));

-- ===== telemetry_data =====
drop policy if exists p_telemetry_driver_insert on public.telemetry_data;
create policy p_telemetry_driver_insert on public.telemetry_data
  for insert to app_driver
  with check (exists (select 1 from public.trips t
                      where t.trip_id = telemetry_data.trip_id
                        and t.driver_id = public.jwt_uid()));

-- PostgREST: pick up the new roles/grants immediately
notify pgrst, 'reload schema';

-- ============================================================================
-- 7b — FLIP `users`  (DO NOT RUN during 7a)
-- Kills the hash hole: anon can no longer read or write password_hash.
-- Edge fns (service role) + users_app view (definer) keep working.
-- ============================================================================
-- PREREQ: web dashboard must be on the secret key FIRST (its login reads users).
-- alter table public.users enable row level security;
-- revoke all on public.users from anon;
-- revoke all on public.users from authenticated;
-- -- rollback: alter table public.users disable row level security;
-- --           grant all on public.users to anon;

-- ============================================================================
-- 7c — FLIP the rest  (DO NOT RUN during 7a)
-- ============================================================================
-- alter table public.trips               enable row level security;
-- alter table public.vehicles            enable row level security;
-- alter table public.driver_availability enable row level security;
-- alter table public.messages            enable row level security;
-- alter table public.bus_checklist       enable row level security;
-- alter table public.maintenance_logs    enable row level security;
-- alter table public.telemetry_data      enable row level security;
-- alter table public.routes              enable row level security;
-- alter table public.fare_config         enable row level security;
-- -- rollback per table: alter table public.<t> disable row level security;

-- ============================================================================
-- 7d — KILL anon  (DO NOT RUN during 7a)
-- ============================================================================
-- revoke all on all tables in schema public from anon;          -- includes users_app view
-- revoke all on all sequences in schema public from anon;
-- revoke all on all tables in schema public from authenticated;
-- -- (web dashboard must already be on the secret key; both apps JWT-only)
-- -- NOTE for future tables (e.g. Phase 8 device_config/device_status): Supabase default
-- -- privileges re-grant anon on CREATE — revoke again per new table.
-- -- rollback: grant select, update on public.<t> to anon;  -- per table as needed
