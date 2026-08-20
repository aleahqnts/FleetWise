-- ============================================================================
-- RouteSync maintenance work orders.
--
-- A row in maintenance_logs is one visit to the shop, and a bus has at most one
-- open at a time. The faults being worked are rows in maintenance_items, one line
-- per fault, so each can be closed on its own and the list always describes the
-- bus as it stands.
--
-- The two tables answer different questions and neither replaces the other.
-- maintenance_logs.issue_details records what a driver's inspection reported at
-- the moment it was submitted and is never rewritten. maintenance_items is the
-- working list an administrator ticks off.
--
-- Criticality lives in checklist_items. An item typed by hand carries none, so it
-- never grounds a bus by itself: grounding stays an explicit act recorded on
-- vehicles.out_of_service, which remains the only gate on dispatch.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Items
-- ---------------------------------------------------------------------------
create table if not exists public.maintenance_items (
  item_id      bigint generated always as identity primary key,
  log_id       int not null references public.maintenance_logs(log_id) on delete cascade,
  label        text not null,
  is_critical  boolean not null default false,   -- from checklist_items; grounds the bus
  source       text not null default 'manual',   -- checklist | manual
  state        text not null default 'open',     -- open | fixed | dismissed
  closed_at    timestamptz,
  closed_by    text,
  note         text,                             -- required when dismissing
  created_at   timestamptz not null default now(),

  constraint maintenance_items_state_check
    check (state in ('open', 'fixed', 'dismissed')),
  constraint maintenance_items_source_check
    check (source in ('checklist', 'manual')),
  -- A fault that grounds the bus is closed by fixing it. Dismissing one would put a
  -- bus back on the road on an opinion that the fault was never real.
  constraint maintenance_items_critical_not_dismissed
    check (not (is_critical and state = 'dismissed'))
);

-- One line per fault, however many times it is reported while the order is open.
create unique index if not exists uq_maintenance_items_label
  on public.maintenance_items (log_id, lower(label));

-- The panel and the close-when-empty rule both ask which items are still open.
create index if not exists idx_maintenance_items_open
  on public.maintenance_items (log_id) where state = 'open';

-- ---------------------------------------------------------------------------
-- 2. Lockdown
-- ---------------------------------------------------------------------------
revoke all on public.maintenance_items from public, anon, authenticated, app_driver, app_camera;
alter table public.maintenance_items enable row level security;
-- No policies on purpose: service_role bypasses RLS and keeps its own grants, and
-- every other role is left holding nothing to bypass.

comment on table public.maintenance_items is
  'The faults being worked under one maintenance_logs order, one row per fault.';
comment on column public.maintenance_items.is_critical is
  'Whether failing this grounds the bus. Set from checklist_items; hand-typed items are never critical.';
comment on column public.maintenance_items.state is
  'open until closed as fixed, or dismissed when the fault was not real.';

-- ---------------------------------------------------------------------------
-- 3. Fold existing open orders into one per bus
-- ---------------------------------------------------------------------------
-- A bus may hold several open orders, which the panel cannot show. The oldest is
-- kept, every other one is emptied onto it, and the donors are closed. The keeper
-- takes the busiest workshop status among them, since a bus booked into the shop
-- stays booked.
do $$
declare
  merged int := 0;
begin
  create temporary table _open_orders on commit drop as
  select log_id,
         vehicle_id,
         created_at,
         issue_details,
         maintenance_status,
         row_number() over (partition by vehicle_id order by created_at, log_id) as rn
  from public.maintenance_logs
  where resolved_at is null
    and vehicle_id is not null;

  -- Every fault named by any open order becomes an item on the keeper.
  insert into public.maintenance_items (log_id, label, is_critical, source, created_at)
  select keeper.log_id,
         trim(fault.label),
         coalesce(ci.is_critical, false)
           -- jsonb_exists rather than the ? operator, which some clients read as a
           -- parameter placeholder.
           or coalesce(jsonb_exists(o.issue_details -> 'critical_issues', trim(fault.label)), false),
         case when ci.item_id is null then 'manual' else 'checklist' end,
         o.created_at
  from _open_orders o
  join _open_orders keeper
    on keeper.vehicle_id = o.vehicle_id and keeper.rn = 1
  cross join lateral jsonb_array_elements_text(
         coalesce(o.issue_details -> 'issues', '[]'::jsonb)) as fault(label)
  left join public.checklist_items ci
    on lower(ci.label) = lower(trim(fault.label))
  where trim(fault.label) <> ''
  on conflict do nothing;

  -- A bus booked into the shop stays booked, whichever order carried that status.
  update public.maintenance_logs m
  set maintenance_status = 'Under Repair'
  from _open_orders keeper
  where m.log_id = keeper.log_id
    and keeper.rn = 1
    and exists (select 1
                from _open_orders d
                where d.vehicle_id = keeper.vehicle_id
                  and d.maintenance_status = 'Under Repair');

  -- The emptied orders are closed, with their faults now carried by the keeper.
  update public.maintenance_logs m
  set resolved_at = now(),
      maintenance_status = 'No Issues',
      remarks = concat_ws(' ', m.remarks,
                          'Folded into work order ' || keeper.log_id || '.')
  from _open_orders d
  join _open_orders keeper
    on keeper.vehicle_id = d.vehicle_id and keeper.rn = 1
  where m.log_id = d.log_id
    and d.rn > 1;

  get diagnostics merged = row_count;
  raise notice 'Folded % order(s) into their vehicle''s oldest open order.', merged;
end $$;

-- ---------------------------------------------------------------------------
-- 4. Check
-- ---------------------------------------------------------------------------
-- Counting from maintenance_logs, and filtering to open orders in the where clause.
-- Reaching for the vehicles table instead lets a bus with no history at all satisfy
-- "resolved_at is null" through the outer join's empty row, and counting rows rather
-- than orders multiplies each order by the items hanging off it.
select m.vehicle_id,
       m.log_id,
       m.maintenance_status,
       count(i.item_id) filter (where i.state = 'open') as open_items,
       count(i.item_id) filter (where i.state = 'open' and i.is_critical) as open_critical
from public.maintenance_logs m
left join public.maintenance_items i on i.log_id = m.log_id
where m.resolved_at is null
group by m.vehicle_id, m.log_id, m.maintenance_status
order by m.vehicle_id;
