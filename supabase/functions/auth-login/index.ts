// auth-login — Phase 7 driver login.
// Body: { email, password } → 200 { token, user } | 401 | 429.
// Verifies the homemade users table's ASP.NET Identity hash SERVER-side and mints a
// 30-day app_driver JWT. Wrong email and wrong password return the same 401 message.
// Deploy with --no-verify-jwt (this IS the login — callers have no JWT yet).

import { createClient } from "npm:@supabase/supabase-js@2";
import {
  CORS_HEADERS,
  json,
  mintJwt,
  nowSec,
  RateLimiter,
  verifyAspNetHash,
} from "../_shared/auth.ts";

const DRIVER_ROLE_ID = 2;
const TOKEN_DAYS = 30;
const limiter = new RateLimiter(5, 15 * 60 * 1000); // 5 fails / 15 min per email

const service = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: CORS_HEADERS });
  if (req.method !== "POST") return json(405, { error: "POST only" });

  const secret = Deno.env.get("JWT_SECRET");
  if (!secret) return json(500, { error: "JWT_SECRET not configured" });

  let email: string, password: string;
  try {
    const body = await req.json();
    email = String(body.email ?? "").trim();
    password = String(body.password ?? "");
  } catch {
    return json(400, { error: "Invalid JSON body" });
  }
  if (!email || !password) return json(400, { error: "email and password required" });

  const rlKey = email.toLowerCase();
  if (limiter.blocked(rlKey)) {
    return json(429, { error: "Too many attempts. Try again in a few minutes." });
  }

  const { data: rows, error } = await service
    .from("users")
    .select("*")
    .eq("email_address", email)
    .limit(1);
  if (error) return json(500, { error: "Lookup failed" });

  const user = rows?.[0];
  const invalid = () => {
    limiter.fail(rlKey);
    return json(401, { error: "Invalid email or password." });
  };

  if (!user || !user.password_hash) return invalid();
  if (user.role_id !== DRIVER_ROLE_ID) return invalid(); // driver app is drivers-only
  if (user.account_status !== "Activated") return invalid();
  if (!(await verifyAspNetHash(password, user.password_hash))) return invalid();

  limiter.clear(rlKey);

  const token = await mintJwt(
    {
      role: "app_driver",
      user_id: user.user_id,
      iat: nowSec(),
      exp: nowSec() + TOKEN_DAYS * 24 * 3600,
    },
    secret,
  );

  // last_login stamp (best-effort; login must not fail on it)
  await service
    .from("users")
    .update({ last_login: new Date().toISOString() })
    .eq("user_id", user.user_id);

  const { password_hash: _hash, ...safeUser } = user;
  return json(200, { token, user: safeUser });
});
