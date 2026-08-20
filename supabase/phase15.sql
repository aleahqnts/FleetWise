-- ============================================================================
-- RouteSync inspection outcomes.
--
-- An inspection has three outcomes, not two. Everything passing is a pass, a
-- critical fault is a failure that grounds the bus, and anything else is a pass
-- carrying defects: the driver works the shift and the faults are reviewed.
--
-- checklist_status_enum holds the first two. This adds the third, so the middle
-- outcome can be stored rather than forced into one of the others, which would
-- either hide a fault or ground a bus that does not need grounding.
-- ============================================================================

alter type public.checklist_status_enum add value if not exists 'Passed with Defects';

-- Check: the labels the column will accept.
select enumlabel as checklist_status
from pg_enum
where enumtypid = 'public.checklist_status_enum'::regtype
order by enumsortorder;
