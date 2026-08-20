-- ============================================================================
-- RouteSync pre-trip inspection: critical and minor items.
--
-- The items a driver inspects live here rather than in the app, so their wording
-- and their weight can change without a new build reaching every phone. Each one
-- carries whether it is critical.
--
-- Critical means the bus cannot be driven safely or legally without it. Failing
-- one blocks the trip and grounds the bus. Failing anything else is a defect: the
-- driver still works the shift and the dashboard raises it for review.
--
-- Both outcomes open a maintenance incident, so both clear the same way, through
-- the vehicles tab.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Table
-- ---------------------------------------------------------------------------
create table if not exists public.checklist_items (
  item_id       int generated always as identity primary key,
  section_key   text not null,     -- matches the bus_checklist json column it lands in
  section_title text not null,     -- heading the driver sees
  label         text not null,
  is_critical   boolean not null default false,
  sort_order    int not null,
  active        boolean not null default true,
  unique (section_key, label)
);

create index if not exists idx_checklist_items_order
  on public.checklist_items (active, sort_order);

-- ---------------------------------------------------------------------------
-- 2. Access
-- ---------------------------------------------------------------------------
-- Drivers read the list to build the form. Nobody but the dashboard writes it,
-- and the dashboard holds the service key, which bypasses row-level security.
revoke all on public.checklist_items from public, anon, app_camera;
grant select on public.checklist_items to app_driver;
alter table public.checklist_items enable row level security;

drop policy if exists checklist_items_read on public.checklist_items;
create policy checklist_items_read on public.checklist_items
  for select to app_driver using (active);

-- ---------------------------------------------------------------------------
-- 3. The items
-- ---------------------------------------------------------------------------
-- Critical set: what stops the bus being driven safely. Brakes and their fluid,
-- tyres, lights, and the driver's own restraint. Everything else is a defect
-- worth reporting but not worth stranding a shift over.
insert into public.checklist_items (section_key, section_title, label, is_critical, sort_order) values
  ('exterior_inspection', 'Exterior Inspection', 'Tires & wheels',                    true,  10),
  ('exterior_inspection', 'Exterior Inspection', 'Lights & signals',                  true,  20),
  ('exterior_inspection', 'Exterior Inspection', 'Mirrors & windshield',              false, 30),
  ('exterior_inspection', 'Exterior Inspection', 'Wipers',                            false, 40),
  ('exterior_inspection', 'Exterior Inspection', 'No visible damage or leaks',        false, 50),

  ('engine_compartment',  'Engine Compartment',  'Engine oil level',                  false, 60),
  ('engine_compartment',  'Engine Compartment',  'Coolant level',                     false, 70),
  ('engine_compartment',  'Engine Compartment',  'Brake fluid level',                 true,  80),
  ('engine_compartment',  'Engine Compartment',  'Battery condition',                 false, 90),

  ('interior_inspection', 'Interior Inspection', 'Driver seat & seatbelt',            true,  100),
  ('interior_inspection', 'Interior Inspection', 'Horn & dashboard gauges',           false, 110),
  ('interior_inspection', 'Interior Inspection', 'Fuel level',                        false, 120),
  ('interior_inspection', 'Interior Inspection', 'Passenger seats, handrails & doors', false, 130),

  ('brake_safety',        'Brake & Safety Systems', 'Service brakes',                 true,  140),
  ('brake_safety',        'Brake & Safety Systems', 'Parking brake',                  true,  150),
  ('brake_safety',        'Brake & Safety Systems', 'Fire extinguisher & first aid kit', false, 160),
  ('brake_safety',        'Brake & Safety Systems', 'Early warning device / reflectors', false, 170)
on conflict (section_key, label) do nothing;

-- Check: what the driver will be shown, and which items block a trip.
select section_title, label, is_critical
from public.checklist_items
where active
order by sort_order;
