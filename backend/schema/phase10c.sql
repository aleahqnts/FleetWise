-- ============================================================================
-- RouteSync Phase 10c - Audit Log page permission (see MOTHER-LOGS-plan.md)
--
-- The viewer is gated on a NEW web permission, "audit", which is deliberately
-- separate from "users": being allowed to manage accounts is not the same
-- decision as being allowed to read the record of what everyone did.
--
-- No role has it until this runs. Permission claims are stamped at sign-in, so
-- anyone already signed in must sign out and back in before the sidebar link
-- and the page appear.
-- ============================================================================

-- 1. Grant it to the top admin role only.
update public.roles
set web_permissions = coalesce(web_permissions, '{}'::jsonb) || '{"audit": true}'::jsonb
where role_name ilike '%admin%';

-- 2. Every other role gets an explicit false, so the toggle renders (unset and
--    off look identical in the UI, but only the explicit value round-trips
--    cleanly through the Manage Roles form).
update public.roles
set web_permissions = coalesce(web_permissions, '{}'::jsonb) || '{"audit": false}'::jsonb
where not (coalesce(web_permissions, '{}'::jsonb) ? 'audit');

-- Check: who can read the trail now.
select role_id, role_name, web_permissions -> 'audit' as audit_access
from public.roles
order by role_id;
