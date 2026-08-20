-- ============================================================================
-- RouteSync retired buses.
--
-- A bus that has left the fleet for good, sold, scrapped or written off, keeps
-- its row. Trips, inspections, maintenance logs and the audit trail all key on
-- vehicle_id, so removing it would orphan the history that explains what the bus
-- did while it was working.
--
-- Retirement is a column of its own rather than a vehicle_status value, for the
-- same reason out_of_service is: vehicle_status describes the current shift and
-- is overwritten by the next one, while this has to outlast every shift.
--
-- Out of service and retired answer different questions. Out of service is a bus
-- that cannot run today and is expected back. Retired is a bus that is not coming
-- back at all.
-- ============================================================================

alter table public.vehicles
  add column if not exists retired_at timestamptz,
  add column if not exists retired_reason text;

-- The registry lists working buses by default, so the filter runs on every load.
create index if not exists idx_vehicles_retired
  on public.vehicles (retired_at);

comment on column public.vehicles.retired_at is
  'When the bus left the fleet for good. Null means it is still in service.';
comment on column public.vehicles.retired_reason is
  'Why it was retired, as entered by an administrator.';

-- Check: the working fleet, and anything already retired.
select
  count(*) filter (where retired_at is null) as in_fleet,
  count(*) filter (where retired_at is not null) as retired
from public.vehicles;
