-- ============================================================================
-- RouteSync Phase 8c — snapshot storage (see REMOTE-CONTROL-plan.md §1 dec 5-9)
--
-- Private bucket `camera-snapshots`, one transient object per device:
--   {device_id}.jpg  — uploaded by the camera on wake, deleted on apply /
--   cancel / ~2 min timeout (camera-driven purge; decision 9).
--
-- Storage API runs requests under the JWT role, so the same app_camera /
-- app_driver roles + helpers from phase7/8a scope the objects.
-- ============================================================================

-- 1. Bucket (private, jpeg-only, 2 MB cap)
insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('camera-snapshots', 'camera-snapshots', false, 2097152, array['image/jpeg'])
on conflict (id) do nothing;

-- 2. Schema/table grants for the app roles (RLS below is the real gate)
grant usage on schema storage to app_camera, app_driver;
grant select, insert, update, delete on storage.objects to app_camera;
grant select on storage.objects to app_driver;
grant select on storage.buckets to app_camera, app_driver;

-- 3. Policies
-- Camera: full control of exactly ONE object — its own {device_id}.jpg.
drop policy if exists p_snap_camera_all on storage.objects;
create policy p_snap_camera_all on storage.objects
  for all to app_camera
  using (bucket_id = 'camera-snapshots' and name = public.jwt_dev() || '.jpg')
  with check (bucket_id = 'camera-snapshots' and name = public.jwt_dev() || '.jpg');

-- Driver: read-only, and only the snapshot of its ACTIVE trip's camera.
drop policy if exists p_snap_driver_read on storage.objects;
create policy p_snap_driver_read on storage.objects
  for select to app_driver
  using (bucket_id = 'camera-snapshots'
         and name = public.driver_active_camera() || '.jpg');

-- Bucket metadata visible to the app roles (some storage paths look it up
-- under the request role).
drop policy if exists p_snap_bucket_read on storage.buckets;
create policy p_snap_bucket_read on storage.buckets
  for select to app_camera, app_driver
  using (id = 'camera-snapshots');

-- anon/authenticated: nothing. (No grants above; default deny.)
