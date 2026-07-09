// device-token — Phase 7 camera-phone provisioning.
// Body: { device_id, fleet_secret } → 200 { token } | 401.
// The installer's bind passcode IS the fleet secret; it lives only in the
// FLEET_BIND_SECRET env var (never in the DB, never in the APK). The JWT carries
// device_id ONLY — vehicle scope comes from the vehicles.counter_device_id join,
// so re-binding to another bus needs no new token.
// Deploy with --no-verify-jwt (callers have no JWT yet).

import { CORS_HEADERS, fixedTimeEquals, json, mintJwt, nowSec, RateLimiter } from "../_shared/auth.ts";

const TOKEN_DAYS = 365;
const DEVICE_ID_RE = /^cam-[0-9a-f]{8}$/;
const limiter = new RateLimiter(5, 15 * 60 * 1000); // 5 fails / 15 min per device

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: CORS_HEADERS });
  if (req.method !== "POST") return json(405, { error: "POST only" });

  const secret = Deno.env.get("JWT_SECRET");
  const fleetSecret = Deno.env.get("FLEET_BIND_SECRET");
  if (!secret || !fleetSecret) return json(500, { error: "Secrets not configured" });

  let deviceId: string, given: string;
  try {
    const body = await req.json();
    deviceId = String(body.device_id ?? "").trim();
    given = String(body.fleet_secret ?? "");
  } catch {
    return json(400, { error: "Invalid JSON body" });
  }
  if (!DEVICE_ID_RE.test(deviceId)) return json(400, { error: "Bad device_id" });
  if (limiter.blocked(deviceId)) {
    return json(429, { error: "Too many attempts. Try again in a few minutes." });
  }

  // Constant-time compare via SHA-256 digests (length-independent).
  const enc = new TextEncoder();
  const [a, b] = await Promise.all([
    crypto.subtle.digest("SHA-256", enc.encode(given)),
    crypto.subtle.digest("SHA-256", enc.encode(fleetSecret)),
  ]);
  if (!fixedTimeEquals(new Uint8Array(a), new Uint8Array(b))) {
    limiter.fail(deviceId);
    return json(401, { error: "Invalid passcode." });
  }

  limiter.clear(deviceId);
  const token = await mintJwt(
    {
      role: "app_camera",
      device_id: deviceId,
      iat: nowSec(),
      exp: nowSec() + TOKEN_DAYS * 24 * 3600,
    },
    secret,
  );
  return json(200, { token });
});
