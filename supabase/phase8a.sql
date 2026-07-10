-- ============================================================================
-- RouteSync Phase 8a — remote camera control: device_config + device_status
-- (see REMOTE-CONTROL-plan.md §3 / §7 for the locked design)
--
-- SECURED AT CREATION: Phase 7d is live, so this script enables RLS and revokes
-- anon/authenticated IMMEDIATELY — Supabase default privileges re-grant them on
-- every CREATE TABLE, and a new table must never reopen an anon hole.
--
-- Depends on phase7.sql helpers: jwt_uid(), jwt_dev(), driver_is_active().
-- Web/admin (8e) uses the service key and bypasses RLS entirely.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Tables
-- ---------------------------------------------------------------------------

-- Desired state. Driver (8b), web/admin (8e), and the camera's own local
-- calibration ALL write here; the camera is a follower that applies whatever
-- row version is newest. Last-write-wins via `version`.
create table if not exists public.device_config (
  device_id   text primary key,            -- = vehicles.counter_device_id
  line_ax real, line_ay real, line_bx real, line_by real,
  inward_sign int not null default 1,
  use_back_camera boolean not null default false,
  wake_requested_at timestamptz,           -- Tier 2 (8c): driver asks camera to wake+snap
  version     int not null default 0,      -- bumped on every write; camera echoes it back
  updated_by  text,                        -- 'driver:<uid>' | 'admin' | 'device'
  updated_at  timestamptz default now()
);

-- Reported state. Camera writes ONLY its own row (no same-row write races with
-- config). Driver shows the ✓ when config_version_applied == device_config.version.
create table if not exists public.device_status (
  device_id   text primary key,
  last_seen   timestamptz,                 -- liveness (like trips.count_heartbeat)
  wake_state  text not null default 'idle',-- idle|capturing|preview|applied (8c)
  snapshot_ready_at timestamptz,
  applied_at  timestamptz,
  config_version_applied int not null default -1
);

-- ---------------------------------------------------------------------------
-- 2. Kill the default grants BEFORE anything else can hit the tables
-- ---------------------------------------------------------------------------
revoke all on public.device_config from anon, authenticated;
revoke all on public.device_status from anon, authenticated;

-- ---------------------------------------------------------------------------
-- 3. RLS on from day one (policies below are the only access)
-- ---------------------------------------------------------------------------
alter table public.device_config enable row level security;
alter table public.device_status enable row level security;

-- ---------------------------------------------------------------------------
-- 4. Helper: which camera device is on MY active trip's bus?
--    SECURITY DEFINER so app_driver policies can join trips+vehicles without
--    widening the driver's own table grants. NULL when no active trip.
-- ---------------------------------------------------------------------------
create or replace function public.driver_active_camera() returns text
language sql stable security definer set search_path = public as $$
  select v.counter_device_id
  from trips t
  join vehicles v on v.vehicle_id = t.vehicle_id
  where t.driver_id = public.jwt_uid()
    and t.trip_status = 'Active'
    and v.counter_device_id is not null
  limit 1
$$;

grant execute on function public.driver_active_camera() to app_driver;

-- ---------------------------------------------------------------------------
-- 5. Grants
-- ---------------------------------------------------------------------------

-- Camera: full own-row control (row scope enforced by RLS). INSERT is needed
-- because the camera SEEDS its config row at first follow (upsert).
grant select, insert, update on public.device_config to app_camera;
grant select, insert, update on public.device_status to app_camera;

-- Driver (8b UI): read both, column-scoped write on config — can never touch
-- device_id (no re-pointing a row at another camera).
grant select on public.device_config to app_driver;
grant update (line_ax, line_ay, line_bx, line_by, inward_sign, use_back_camera,
              wake_requested_at, version, updated_by, updated_at)
  on public.device_config to app_driver;
grant select on public.device_status to app_driver;

-- ---------------------------------------------------------------------------
-- 6. Policies
-- ---------------------------------------------------------------------------

-- ===== device_config =====
drop policy if exists p_devcfg_camera_all on public.device_config;
create policy p_devcfg_camera_all on public.device_config
  for all to app_camera
  using (device_id = public.jwt_dev())
  with check (device_id = public.jwt_dev());

drop policy if exists p_devcfg_driver_select on public.device_config;
create policy p_devcfg_driver_select on public.device_config
  for select to app_driver
  using (device_id = public.driver_active_camera());

drop policy if exists p_devcfg_driver_update on public.device_config;
create policy p_devcfg_driver_update on public.device_config
  for update to app_driver
  using (device_id = public.driver_active_camera() and public.driver_is_active())
  with check (device_id = public.driver_active_camera());

-- ===== device_status =====
drop policy if exists p_devstat_camera_all on public.device_status;
create policy p_devstat_camera_all on public.device_status
  for all to app_camera
  using (device_id = public.jwt_dev())
  with check (device_id = public.jwt_dev());

drop policy if exists p_devstat_driver_select on public.device_status;
create policy p_devstat_driver_select on public.device_status
  for select to app_driver
  using (device_id = public.driver_active_camera());

-- PostgREST: pick up the new tables/grants immediately
notify pgrst, 'reload schema';
