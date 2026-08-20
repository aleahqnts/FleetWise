// password-reset-complete: step three of recovering a forgotten driver password.
// Body: { reset_token, new_password } -> 200 { ok: true } | 400 | 401.
// Deploy with --no-verify-jwt (the token here is the reset token, not a session).
//
// Hashes the new password server-side in the same ASP.NET Identity v3 format the
// web dashboard reads, so both surfaces stay in step. The driver signs in with
// the new password afterwards rather than being handed a session, which keeps
// this endpoint from being a second way to obtain a driver token.

import { createClient } from "npm:@supabase/supabase-js@2";
import { CORS_HEADERS, hashAspNetV3, json, verifyJwt } from "../_shared/auth.ts";
import { passwordProblem } from "../_shared/password.ts";
import { audit } from "../_shared/audit.ts";
import { actorFor } from "../_shared/actor.ts";

// Mirrors FleetWise.Services.PasswordPolicy.TemporaryPassword. Landing on this
// value would send the driver straight back to the forced-change screen on the
// next sign-in, so it is refused here rather than becoming a loop.
const TEMPORARY_PASSWORD = "@Temp123";

const service = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: CORS_HEADERS });
  if (req.method !== "POST") return json(405, { error: "POST only" });

  const secret = Deno.env.get("JWT_SECRET");
  if (!secret) return json(500, { error: "JWT_SECRET not configured" });

  let token: string, newPwd: string;
  try {
    const body = await req.json();
    token = String(body.reset_token ?? "");
    newPwd = String(body.new_password ?? "");
  } catch {
    return json(400, { error: "Invalid JSON body" });
  }
  const claims = token ? await verifyJwt(token, secret) : null;
  if (
    !claims || claims.purpose !== "pwd_reset" ||
    typeof claims.user_id !== "number" || typeof claims.rid !== "number"
  ) {
    return json(401, { error: "Start the reset again." });
  }
  const userId = claims.user_id as number;
  const rid = claims.rid as number;

  const problem = passwordProblem(newPwd);
  if (problem) return json(400, { error: problem });
  if (newPwd === TEMPORARY_PASSWORD) {
    return json(400, { error: "Choose a password different from the temporary one." });
  }

  // The token is only half the check. The row it names must still be an accepted,
  // unspent reset belonging to this driver, which is what makes it single use.
  const { data: rows, error } = await service
    .from("password_reset_otp")
    .select("id, user_id, consumed_at, completed_at")
    .eq("id", rid)
    .limit(1);
  if (error) return json(500, { error: "Lookup failed" });

  const row = rows?.[0];
  if (!row || row.user_id !== userId || !row.consumed_at || row.completed_at) {
    await audit(req, {
      action: "password_reset_failed",
      actorType: "user",
      actorRole: null,
      actorId: userId,
      targetTable: "users",
      targetId: userId,
      outcome: "denied",
      summary: `Reset token refused for user ${userId} (already used or unknown)`,
    });
    return json(401, { error: "Start the reset again." });
  }

  const { data: userRows, error: userErr } = await service
    .from("users")
    .select("user_id, role_id, account_status")
    .eq("user_id", userId)
    .limit(1);
  if (userErr) return json(500, { error: "Lookup failed" });
  const user = userRows?.[0];
  if (user?.account_status !== "Activated") {
    return json(401, { error: "Start the reset again." });
  }

  // Recorded from the account's role, since the endpoint serves both surfaces.
  const actor = await actorFor(service, user.role_id);

  const newHash = await hashAspNetV3(newPwd);
  const { error: pwErr } = await service
    .from("users")
    .update({ password_hash: newHash, updated_at: new Date().toISOString() })
    .eq("user_id", userId);
  if (pwErr) return json(500, { error: "Update failed" });

  // Spend this reset, and retire any other code outstanding for the driver: once
  // the password has changed, an older code in an older mail must not still work.
  const now = new Date().toISOString();
  await service
    .from("password_reset_otp")
    .update({ completed_at: now, consumed_at: now })
    .eq("user_id", userId)
    .is("completed_at", null);

  // The users trigger also records that the hash changed, with no values. This
  // row adds who asked and by which route.
  await audit(req, {
    action: "password_reset_completed",
    actorType: actor.actorType,
    actorRole: actor.actorRole,
    actorId: userId,
    targetTable: "users",
    targetId: userId,
    summary: `User ${userId} set a new password after an emailed code`,
  });

  return json(200, { ok: true });
});
