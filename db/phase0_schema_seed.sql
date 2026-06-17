-- FleetWise / RouteSync Driver App — Phase 0: schema delta + seed
-- Run in the Supabase SQL editor (Dashboard -> SQL Editor).

-- =========================================================
-- 1) SCHEMA DELTA (safe to re-run)
-- =========================================================

alter table trips
  add column if not exists total_boarded     int  not null default 0,
  add column if not exists actual_start_time timestamptz,
  add column if not exists actual_end_time   timestamptz;
-- estimated_revenue already exists on trips.

create table if not exists fare_config (
  id            int primary key default 1,
  standard_fare numeric(10,2) not null,
  updated_at    timestamptz default now()
);

insert into fare_config (id, standard_fare)
values (1, 15.00)
on conflict (id) do nothing;

-- trip_id generation: surrogate running number, prefix only (date/bus live in
-- their own columns). Compact, unique, ordered, never collides. -> TRIP026001
create sequence if not exists trip_seq start 26001;

alter table trips
  alter column trip_id set default 'TRIP' || lpad(nextval('trip_seq')::text, 6, '0');
-- NOTE: dashboard must NOT supply trip_id on insert; let this default fire.

-- =========================================================
-- 2) SEED TEST DATA
-- Driver = users.user_id (role_id=2). Helper queries:
--   select user_id, email_address from users where role_id = 2 and account_status = 'Activated';
--   select route_id, route_name from routes;
--   select vehicle_id, plate_number from vehicles;
-- =========================================================

-- Seed one trip for TODAY (trip_id auto-generated). Adjust IDs to your data.
insert into trips
  (date, shift_type, shift_start_time, shift_end_time,
   route_id, vehicle_id, driver_id, trip_status, estimated_revenue, total_boarded)
values
  (current_date, 'Morning', '06:00', '14:00',
   1, 'V001', 2, 'Not Yet Started', 0, 0);

-- trip_status values used by the system:
--   'Pending', 'Not Yet Started', 'Assignment Issue', 'Active', 'Completed'
-- The app sets 'Active' on Start Trip and 'Completed' on End Trip.
