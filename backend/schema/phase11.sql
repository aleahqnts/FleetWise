-- ============================================================================
-- RouteSync password reset by emailed one-time code.
--
-- A driver who cannot sign in asks for a code, proves control of the mailbox on
-- file, and then sets their own password from the app. No password ever travels
-- by email, and no administrator is in the loop.
--
-- password_reset_otp holds one row per request. The code itself is never stored:
-- the row keeps an HMAC of it, so a stolen copy of this table cannot be replayed
-- against the live system. Rows survive their use because they double as the
-- rate limiter, counting recent requests per driver, per address, and fleet-wide.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1. Table
-- ---------------------------------------------------------------------------
create table if not exists public.password_reset_otp (
  id           bigint generated always as identity primary key,
  user_id      int not null references public.users(user_id) on delete cascade,
  otp_hash     text not null,               -- HMAC-SHA256, never the code itself
  expires_at   timestamptz not null,
  attempts     int not null default 0,      -- wrong guesses; the row dies at 5
  consumed_at  timestamptz,                 -- code accepted, reset token issued
  completed_at timestamptz,                 -- password changed, token spent
  created_at   timestamptz not null default now(),
  ip           text
);

create index if not exists idx_pwreset_user on public.password_reset_otp (user_id, created_at desc);
create index if not exists idx_pwreset_time on public.password_reset_otp (created_at desc);
create index if not exists idx_pwreset_ip   on public.password_reset_otp (ip, created_at desc);

-- ---------------------------------------------------------------------------
-- 2. Lockdown
-- ---------------------------------------------------------------------------
-- Edge functions only. The hashes and timings in this table are precisely what
-- an attacker would want, and no client has any reason to read or write it.
revoke all on public.password_reset_otp from public, anon, authenticated, app_driver, app_camera;
alter table public.password_reset_otp enable row level security;
-- No policies on purpose: service_role bypasses RLS and keeps its own grants,
-- while every other role is left holding nothing to bypass.

-- ---------------------------------------------------------------------------
-- 3. Housekeeping
-- ---------------------------------------------------------------------------
-- Rows older than the widest rate-limit window (24 hours) no longer influence
-- any decision. Safe to run by hand or from a scheduled job.
--
--   delete from public.password_reset_otp where created_at < now() - interval '7 days';

select 'password_reset_otp ready' as status;
