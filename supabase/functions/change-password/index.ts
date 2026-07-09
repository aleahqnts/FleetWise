// change-password — Phase 7. Removes the last client-side hashing site.
// Auth: Authorization: Bearer <app_driver JWT> (self-verified — deploy --no-verify-jwt).
// Body: { old_password, new_password } → 200 | 400 | 401.
// Verifies the old password against the stored hash, hashes the new one SERVER-side in
// the same ASP.NET Identity v3 format (web dashboard login stays compatible).

import { createClient } from "npm:@supabase/supabase-js@2";
import {
  CORS_HEADERS,
  hashAspNetV3,
  json,
  verifyAspNetHash,
  verifyJwt,
} from "../_shared/auth.ts";

const service = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: CORS_HEADERS });
  if (req.method !== "POST") return json(405, { error: "POST only" });

  const secret = Deno.env.get("JWT_SECRET");
  if (!secret) return json(500, { error: "JWT_SECRET not configured" });

  const bearer = (req.headers.get("Authorization") ?? "").replace(/^Bearer\s+/i, "");
  const claims = bearer ? await verifyJwt(bearer, secret) : null;
  if (!claims || claims.role !== "app_driver" || typeof claims.user_id !== "number") {
    return json(401, { error: "Not signed in." });
  }
  const userId = claims.user_id;

  let oldPwd: string, newPwd: string;
  try {
    const body = await req.json();
    oldPwd = String(body.old_password ?? "");
    newPwd = String(body.new_password ?? "");
  } catch {
    return json(400, { error: "Invalid JSON body" });
  }
  if (newPwd.length < 6) return json(400, { error: "New password too short (min 6)." });

  const { data: rows, error } = await service
    .from("users")
    .select("user_id, password_hash, account_status")
    .eq("user_id", userId)
    .limit(1);
  if (error) return json(500, { error: "Lookup failed" });

  const user = rows?.[0];
  if (!user || user.account_status !== "Activated") return json(401, { error: "Not signed in." });
  if (!user.password_hash || !(await verifyAspNetHash(oldPwd, user.password_hash))) {
    return json(400, { error: "Current password is incorrect." });
  }

  const newHash = await hashAspNetV3(newPwd);
  const { error: upErr } = await service
    .from("users")
    .update({ password_hash: newHash, updated_at: new Date().toISOString() })
    .eq("user_id", userId);
  if (upErr) return json(500, { error: "Update failed" });

  return json(200, { ok: true });
});
