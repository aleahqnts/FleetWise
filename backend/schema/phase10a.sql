-- ============================================================================
-- RouteSync Phase 10a — "Mother Logs" audit trail (see MOTHER-LOGS-plan.md)
--
-- audit_log: append-only, server-authoritative. Writers: the two DB triggers
-- below (mobile actions, actor from JWT claims), edge fns (auth events), and
-- the web server (admin actions, 10b) — both via the service key.
--
-- APPEND-ONLY, enforced in two layers:
--   1. privileges — RLS on, zero app-role grants, UPDATE/DELETE revoked from
--      service_role, so no application path can rewrite history; and
--   2. BEFORE triggers that raise on UPDATE/DELETE/TRUNCATE, which bind even the
--      table owner and superusers (the dashboard SQL editor connects as
--      `postgres` and sails straight past layer 1).
-- Removing the trail now requires deliberately dropping a guard trigger.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Table
-- ---------------------------------------------------------------------------
create table if not exists public.audit_log (
  id           bigint generated always as identity primary key,
  occurred_at  timestamptz not null default now(),
  actor_type   text not null,              -- user | device | admin | system | anon
  actor_id     text,                        -- user_id | device_id | null
  actor_role   text,                        -- app_driver | app_camera | service_role | ...
  action       text not null,              -- login | login_failed | token_mint | insert | update | ...
  target_table text,
  target_id    text,
  source       text not null,              -- db | edge | web
  outcome      text not null default 'ok', -- ok | denied | error
  summary      text,                        -- human line
  changes      jsonb,                       -- {old:{...}, new:{...}} (denylisted)
  ip           text,
  request_id   text
);

create index if not exists idx_audit_time    on public.audit_log (occurred_at desc);
create index if not exists idx_audit_actor   on public.audit_log (actor_id, occurred_at desc);
create index if not exists idx_audit_target  on public.audit_log (target_table, target_id);
create index if not exists idx_audit_action  on public.audit_log (action, occurred_at desc);

-- ---------------------------------------------------------------------------
-- 2. Append-only lockdown
-- ---------------------------------------------------------------------------
revoke all on public.audit_log from public, anon, authenticated, app_driver, app_camera;
-- service key may read (viewer) + insert (edge/web writers), never rewrite:
revoke update, delete, truncate, references, trigger on public.audit_log from service_role;
alter table public.audit_log enable row level security;
-- No policies on purpose: app roles have no grants at all; service_role bypasses
-- RLS but holds only SELECT + INSERT privileges.

-- Privileges are NOT enough: the table owner and any superuser (that includes the
-- dashboard SQL editor, which connects as `postgres`) bypass GRANT/REVOKE and RLS
-- entirely — verified the hard way, a DELETE from the SQL editor succeeded. A
-- BEFORE trigger fires regardless of privilege level, so this is what actually
-- makes the table append-only. Row triggers don't see TRUNCATE, hence the third.
create or replace function public.audit_log_immutable() returns trigger
language plpgsql as $$
begin
  raise exception 'audit_log is append-only: % is not permitted', TG_OP
    using hint = 'History cannot be rewritten or trimmed. Removing this guard '
               || 'requires explicitly dropping/disabling the trigger, which is '
               || 'itself a deliberate, visible act.';
end $$;

drop trigger if exists trg_audit_log_no_update on public.audit_log;
create trigger trg_audit_log_no_update
  before update on public.audit_log
  for each row execute function public.audit_log_immutable();

drop trigger if exists trg_audit_log_no_delete on public.audit_log;
create trigger trg_audit_log_no_delete
  before delete on public.audit_log
  for each row execute function public.audit_log_immutable();

drop trigger if exists trg_audit_log_no_truncate on public.audit_log;
create trigger trg_audit_log_no_truncate
  before truncate on public.audit_log
  for each statement execute function public.audit_log_immutable();

-- NOTE (locked #6): NEVER add this table to any retention/prune job
-- (TelemetryRetentionService or otherwise).

-- ---------------------------------------------------------------------------
-- 3. Generic trigger fn — actor read from the REQUEST JWT, never client data.
--    Denylist: password_hash never enters the log (locked #3).
-- ---------------------------------------------------------------------------
create or replace function public.audit_row_change() returns trigger
language plpgsql security definer set search_path = public as $$
declare
  v_claims json;
  v_role   text;
  v_actor_type text;
  v_actor_id   text;
  v_old jsonb;
  v_new jsonb;
  v_pk  text;
  v_summary text;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');

  v_actor_type := case v_role
    when 'app_driver'   then 'user'
    when 'app_camera'   then 'device'
    when 'service_role' then 'admin'   -- web/edge via service key (10b adds the admin name)
    when 'anon'         then 'anon'
    else 'system'
  end;
  v_actor_id := coalesce(v_claims->>'user_id', v_claims->>'device_id');

  if TG_OP <> 'INSERT' then v_old := to_jsonb(OLD) - 'password_hash'; end if;
  if TG_OP <> 'DELETE' then v_new := to_jsonb(NEW) - 'password_hash'; end if;

  v_pk := coalesce(v_new->>'user_id', v_old->>'user_id',
                   v_new->>'device_id', v_old->>'device_id');

  -- Human line. device_config gets a purpose-built one: "Update on device_config"
  -- means nothing to an auditor, but "counting line changed" is the event that
  -- moves passenger counts (and therefore revenue figures).
  if TG_TABLE_NAME = 'device_config' then
    v_summary := 'Camera ' || coalesce(v_pk, '?') || ': '
      || case
           when TG_OP = 'INSERT' then 'config created'
           when TG_OP = 'DELETE' then 'config deleted'
           when (v_old->>'line_ax') is distinct from (v_new->>'line_ax')
             or (v_old->>'line_ay') is distinct from (v_new->>'line_ay')
             or (v_old->>'line_bx') is distinct from (v_new->>'line_bx')
             or (v_old->>'line_by') is distinct from (v_new->>'line_by')
             then 'counting line moved'
           when (v_old->>'inward_sign') is distinct from (v_new->>'inward_sign')
             then 'boarding side flipped'
           when (v_old->>'use_back_camera') is distinct from (v_new->>'use_back_camera')
             then 'lens switched'
           else 'config changed'
         end
      || ' (v' || coalesce(v_new->>'version', v_old->>'version', '?')
      || ', by ' || coalesce(v_new->>'updated_by', 'unknown') || ')';
  else
    v_summary := initcap(TG_OP) || ' on ' || TG_TABLE_NAME || ' ' || coalesce(v_pk, '?')
      || case when v_actor_id is not null
              then ' by ' || v_actor_type || ' ' || v_actor_id else '' end;
  end if;

  insert into public.audit_log
    (actor_type, actor_id, actor_role, action, target_table, target_id,
     source, outcome, summary, changes)
  values
    (v_actor_type, v_actor_id, v_role, lower(TG_OP), TG_TABLE_NAME, v_pk,
     'db', 'ok', v_summary,
     jsonb_strip_nulls(jsonb_build_object('old', v_old, 'new', v_new)));

  if TG_OP = 'DELETE' then return OLD; end if;
  return NEW;
end $$;

-- The fn runs as its owner; app roles never need (or get) audit_log grants.

-- ---------------------------------------------------------------------------
-- 4. Attach to the crown jewels ONLY (locked #2): users + device_config
-- ---------------------------------------------------------------------------

-- users: INSERT/DELETE always; UPDATE only when something OTHER than the
-- login stamp / touch column changed — else every driver login (last_login)
-- would add a duplicate row next to the edge fn's own 'login' entry.
drop trigger if exists trg_audit_users_ins_del on public.users;
create trigger trg_audit_users_ins_del
  after insert or delete on public.users
  for each row execute function public.audit_row_change();

drop trigger if exists trg_audit_users_upd on public.users;
create trigger trg_audit_users_upd
  after update on public.users
  for each row
  when ((to_jsonb(old) - 'password_hash' - 'last_login' - 'updated_at')
        is distinct from
        (to_jsonb(new) - 'password_hash' - 'last_login' - 'updated_at'))
  execute function public.audit_row_change();

-- Password changes still get logged: the hash itself is denylisted from the
-- diff, but a hash change alone would be filtered by the WHEN above — so log
-- it as its own slim row (no values, just the fact).
create or replace function public.audit_password_change() returns trigger
language plpgsql security definer set search_path = public as $$
declare
  v_claims json;
  v_role   text;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');
  insert into public.audit_log
    (actor_type, actor_id, actor_role, action, target_table, target_id, source, outcome, summary)
  values
    (case v_role when 'app_driver' then 'user' when 'service_role' then 'admin' else 'system' end,
     v_claims->>'user_id', v_role, 'password_hash_changed', 'users', new.user_id::text,
     'db', 'ok', 'Password hash changed for user ' || new.user_id);
  return new;
end $$;

drop trigger if exists trg_audit_users_pwd on public.users;
create trigger trg_audit_users_pwd
  after update of password_hash on public.users
  for each row
  when (old.password_hash is distinct from new.password_hash)
  execute function public.audit_password_change();

-- device_config: log real CALIBRATION changes (line coords, lens, sign, version).
-- Ignore `wake_requested_at`-only writes: that column is a doorbell — the web
-- calibrator PATCHes it for every "take a fresh photo", so logging it would bury
-- the actual line changes under snapshot churn (and would make web look noisy
-- next to mobile, which only writes on Save). Same rationale as last_login above.
-- `updated_at` alone likewise proves nothing.
drop trigger if exists trg_audit_devcfg_ins_del on public.device_config;
create trigger trg_audit_devcfg_ins_del
  after insert or delete on public.device_config
  for each row execute function public.audit_row_change();

drop trigger if exists trg_audit_devcfg_upd on public.device_config;
create trigger trg_audit_devcfg_upd
  after update on public.device_config
  for each row
  when ((to_jsonb(old) - 'wake_requested_at' - 'updated_at')
        is distinct from
        (to_jsonb(new) - 'wake_requested_at' - 'updated_at'))
  execute function public.audit_row_change();

notify pgrst, 'reload schema';
