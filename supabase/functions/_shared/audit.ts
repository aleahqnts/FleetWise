// Phase 10a — "Mother Logs" edge-side writer (see MOTHER-LOGS-plan.md).
// Auth events are the one thing the DB triggers cannot see: a failed login never
// touches a row, and a token mint touches nothing at all. These are logged here,
// server-side, with the service key.
//
// Rule: logging must NEVER break the auth flow. Every failure is swallowed — a
// login still succeeds (or still fails) exactly as it would with no audit table.

import { createClient } from "npm:@supabase/supabase-js@2";

const service = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

export type AuditEntry = {
  action: string;              // login | login_failed | token_mint | token_refused | change_password
  actorType?: string;          // user | device | anon | system
  actorId?: string | number | null;
  actorRole?: string | null;   // app_driver | app_camera | null
  targetTable?: string | null;
  targetId?: string | number | null;
  outcome?: "ok" | "denied" | "error";
  summary?: string;
};

/** Caller IP, best-effort (proxied: first hop in x-forwarded-for). */
export function clientIp(req: Request): string | null {
  const fwd = req.headers.get("x-forwarded-for");
  if (fwd) return fwd.split(",")[0].trim();
  return req.headers.get("cf-connecting-ip");
}

/**
 * Append one audit row. Awaited so the write is guaranteed to land before the
 * function returns (edge runtimes can kill a floating promise), but any error
 * is swallowed: the audit trail must never be able to lock anyone out.
 */
export async function audit(req: Request, e: AuditEntry): Promise<void> {
  try {
    await service.from("audit_log").insert({
      actor_type: e.actorType ?? "anon",
      actor_id: e.actorId == null ? null : String(e.actorId),
      actor_role: e.actorRole ?? null,
      action: e.action,
      target_table: e.targetTable ?? null,
      target_id: e.targetId == null ? null : String(e.targetId),
      source: "edge",
      outcome: e.outcome ?? "ok",
      summary: e.summary ?? null,
      ip: clientIp(req),
    });
  } catch {
    // Swallowed on purpose (see header).
  }
}
