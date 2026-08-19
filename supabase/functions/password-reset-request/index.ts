// password-reset-request: step one of recovering a forgotten password.
// Body: { email } -> 200 { ok: true }, or 429 when a shared limit is hit.
// Deploy with --no-verify-jwt (the caller is locked out of the app by definition).
//
// Serves both surfaces: drivers in the mobile app and staff on the dashboard. The
// account's role decides only which one the email tells them to go back to.
//
// Mails a six-digit code to the address on the account and records an HMAC of it.
// The response is 200 whatever the lookup finds: an unknown address, a suspended
// account and a mail outage all look identical from outside, so this endpoint
// cannot be used to discover who holds an account. The real reason lands in the
// audit trail instead.
//
// Response timing still differs slightly between a hit and a miss, since a hit
// calls out to the mail provider. That is accepted: staff addresses are already
// visible to every administrator, so timing buys an attacker nothing new.

import { createClient } from "npm:@supabase/supabase-js@2";
import { CORS_HEADERS, hmacHex, json } from "../_shared/auth.ts";
import { audit, clientIp } from "../_shared/audit.ts";
import { sendMail } from "../_shared/email.ts";

const DRIVER_ROLE_ID = 2;
const CODE_TTL_MIN = 10;

// Limits are counted from password_reset_otp rather than held in memory, because
// edge isolates come and go and an in-memory counter resets with them.
const PER_USER = { max: 3, minutes: 15 };     // one account asking over and over
const PER_IP = { max: 10, minutes: 60 };      // one machine working through addresses
const GLOBAL = { max: 50, minutes: 60 * 24 }; // whole fleet, keeps the mail quota safe

const service = createClient(
  Deno.env.get("SUPABASE_URL")!,
  Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
);

/** A uniformly distributed six-digit code. */
function generateCode(): string {
  // Rejection sampling: a plain modulo of a 32-bit value would make the low end
  // of the range very slightly more likely than the high end.
  const limit = 4_294_000_000; // largest multiple of 1e6 below 2^32
  const buf = new Uint32Array(1);
  let v: number;
  do {
    crypto.getRandomValues(buf);
    v = buf[0];
  } while (v >= limit);
  return String(v % 1_000_000).padStart(6, "0");
}

function since(minutes: number): string {
  return new Date(Date.now() - minutes * 60_000).toISOString();
}

async function countSince(
  column: "user_id" | "ip" | null,
  value: string | number | null,
  minutes: number,
): Promise<number> {
  let q = service
    .from("password_reset_otp")
    .select("id", { count: "exact", head: true })
    .gte("created_at", since(minutes));
  if (column && value !== null) q = q.eq(column, value);
  const { count, error } = await q;
  // A failed count must not open the gate, so it reads as "limit reached".
  if (error) return Number.MAX_SAFE_INTEGER;
  return count ?? 0;
}

function codeMail(code: string, firstName: string | null, roleId: number) {
  const who = firstName ? `Hi ${firstName},` : "Hi,";
  // Named from the stored role rather than anything the caller sent, so the mail
  // cannot be made to point someone at the wrong surface.
  const where = roleId === DRIVER_ROLE_ID ? "the driver app" : "the operator dashboard";

  const text = [
    who,
    "",
    `Your RouteSync password reset code is ${code}`,
    "",
    `The code expires in ${CODE_TTL_MIN} minutes and can be used once.`,
    `Enter it in ${where} to choose a new password.`,
    "",
    "If you did not ask for this, you can ignore this message. Your password has not changed.",
    "",
    "RouteSync",
  ].join("\n");

  const html = `
<div style="font-family:Segoe UI,Arial,sans-serif;color:#1b2a56;line-height:1.5">
  <p>${who}</p>
  <p>Your RouteSync password reset code is:</p>
  <p style="font-size:30px;font-weight:700;letter-spacing:6px;color:#2e9e8f;margin:18px 0">${code}</p>
  <p>The code expires in ${CODE_TTL_MIN} minutes and can be used once.
     Enter it in ${where} to choose a new password.</p>
  <p style="color:#6b7280;font-size:13px">If you did not ask for this, you can ignore this
     message. Your password has not changed.</p>
  <p style="color:#6b7280;font-size:13px">RouteSync</p>
</div>`.trim();

  return { text, html };
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: CORS_HEADERS });
  if (req.method !== "POST") return json(405, { error: "POST only" });

  const secret = Deno.env.get("JWT_SECRET");
  if (!secret) return json(500, { error: "JWT_SECRET not configured" });

  let email: string;
  try {
    const body = await req.json();
    email = String(body.email ?? "").trim();
  } catch {
    return json(400, { error: "Invalid JSON body" });
  }
  if (!email) return json(400, { error: "email required" });

  const ip = clientIp(req);
  const ok = () => json(200, { ok: true });

  // Shared limits are answered honestly with a 429: they say nothing about any
  // particular account, and a caller who trips one deserves to know why.
  if (await countSince(null, null, GLOBAL.minutes) >= GLOBAL.max) {
    await audit(req, {
      action: "password_reset_requested",
      outcome: "denied",
      summary: `Reset for ${email} blocked by the fleet-wide daily limit`,
    });
    return json(429, { error: "Too many reset requests today. Ask your operator for help." });
  }
  if (ip && await countSince("ip", ip, PER_IP.minutes) >= PER_IP.max) {
    await audit(req, {
      action: "password_reset_requested",
      outcome: "denied",
      summary: `Reset for ${email} blocked by the per-address hourly limit`,
    });
    return json(429, { error: "Too many reset requests. Try again later." });
  }

  const { data: rows, error } = await service
    .from("users")
    .select("user_id, first_name, last_name, email_address, role_id, account_status")
    .eq("email_address", email)
    .limit(1);
  if (error) return json(500, { error: "Lookup failed" });

  const user = rows?.[0];

  // Everything below answers 200. Only the audit row says what really happened.
  const quietly = async (reason: string) => {
    await audit(req, {
      action: "password_reset_requested",
      actorType: user ? "user" : "anon",
      actorId: user?.user_id ?? null,
      targetTable: "users",
      targetId: user?.user_id ?? null,
      outcome: "denied",
      summary: `No reset code sent for ${email} (${reason})`,
    });
    return ok();
  };

  if (!user) return await quietly("no such account");
  if (user.account_status !== "Activated") return await quietly(`account ${user.account_status}`);
  if (!user.email_address) return await quietly("no address on file");

  if (await countSince("user_id", user.user_id, PER_USER.minutes) >= PER_USER.max) {
    // Deliberately not a 429: this limit is per account, so answering it
    // differently from an unknown address would confirm the account exists.
    return await quietly("per-account limit reached");
  }

  const code = generateCode();
  const otpHash = await hmacHex(secret, `otp:${user.user_id}:${code}`);

  const { error: insErr } = await service.from("password_reset_otp").insert({
    user_id: user.user_id,
    otp_hash: otpHash,
    expires_at: new Date(Date.now() + CODE_TTL_MIN * 60_000).toISOString(),
    ip,
  });
  if (insErr) return await quietly("could not record the code");

  const mail = codeMail(code, user.first_name, user.role_id);
  const sent = await sendMail({
    to: user.email_address,
    toName: `${user.first_name ?? ""} ${user.last_name ?? ""}`.trim() || undefined,
    subject: "Your RouteSync password reset code",
    html: mail.html,
    text: mail.text,
  });

  await audit(req, {
    action: "password_reset_requested",
    actorType: "user",
    actorId: user.user_id,
    targetTable: "users",
    targetId: user.user_id,
    outcome: sent ? "ok" : "error",
    summary: sent
      ? `Reset code sent to ${email}`
      : `Reset code generated for ${email} but the mail provider refused it`,
  });

  return ok();
});
