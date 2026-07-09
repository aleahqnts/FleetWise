// Shared auth helpers for RouteSync edge functions (Phase 7).
// - ASP.NET Core Identity PasswordHasher v2/v3 verify + v3 hash (crypto.subtle PBKDF2)
// - Minimal HS256 JWT mint/verify (no external deps; PostgREST only needs HS256)

// ---------- base64 helpers ----------

export function b64ToBytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

export function bytesToB64(bytes: Uint8Array): string {
  let bin = "";
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin);
}

function b64url(bytes: Uint8Array): string {
  return bytesToB64(bytes).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function b64urlToBytes(s: string): Uint8Array {
  const pad = s.length % 4 === 0 ? "" : "=".repeat(4 - (s.length % 4));
  return b64ToBytes(s.replace(/-/g, "+").replace(/_/g, "/") + pad);
}

// Constant-time byte comparison.
export function fixedTimeEquals(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
  return diff === 0;
}

// ---------- ASP.NET Core Identity password hashes ----------
//
// v3 layout (marker 0x01):
//   [0]      0x01
//   [1..4]   prf     (uint32 BE: 0=HMACSHA1, 1=HMACSHA256, 2=HMACSHA512)
//   [5..8]   iterations (uint32 BE)
//   [9..12]  salt length (uint32 BE)
//   [13..]   salt, then subkey (rest)
// v2 layout (marker 0x00): PBKDF2-HMAC-SHA1, 1000 iterations, 16B salt, 32B subkey.

const PRF_NAMES = ["SHA-1", "SHA-256", "SHA-512"];

async function pbkdf2(
  password: string,
  salt: Uint8Array,
  iterations: number,
  hash: string,
  lengthBytes: number,
): Promise<Uint8Array> {
  const key = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(password),
    "PBKDF2",
    false,
    ["deriveBits"],
  );
  const bits = await crypto.subtle.deriveBits(
    { name: "PBKDF2", salt: salt as BufferSource, iterations, hash },
    key,
    lengthBytes * 8,
  );
  return new Uint8Array(bits);
}

function readUint32BE(b: Uint8Array, offset: number): number {
  return (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];
}

/** Verify a password against an ASP.NET Identity v2/v3 hash (base64). */
export async function verifyAspNetHash(password: string, hashedB64: string): Promise<boolean> {
  let decoded: Uint8Array;
  try {
    decoded = b64ToBytes(hashedB64);
  } catch {
    return false;
  }
  if (decoded.length < 1) return false;

  if (decoded[0] === 0x00) {
    // v2: SHA1, 1000 iterations, salt[1..16], subkey[17..48]
    if (decoded.length !== 49) return false;
    const salt = decoded.slice(1, 17);
    const expected = decoded.slice(17, 49);
    const actual = await pbkdf2(password, salt, 1000, "SHA-1", 32);
    return fixedTimeEquals(actual, expected);
  }

  if (decoded[0] === 0x01) {
    // v3: self-describing header
    if (decoded.length < 13) return false;
    const prf = readUint32BE(decoded, 1);
    const iterations = readUint32BE(decoded, 5);
    const saltLen = readUint32BE(decoded, 9);
    if (prf > 2 || saltLen < 8 || iterations < 1) return false;
    if (decoded.length < 13 + saltLen + 16) return false;
    const salt = decoded.slice(13, 13 + saltLen);
    const expected = decoded.slice(13 + saltLen);
    const actual = await pbkdf2(password, salt, iterations, PRF_NAMES[prf], expected.length);
    return fixedTimeEquals(actual, expected);
  }

  return false;
}

/** Hash a new password in v3 format (HMAC-SHA512, 100k iterations) — verifiable by
 *  the web dashboard + driver app PasswordHasher (format is self-describing). */
export async function hashAspNetV3(password: string): Promise<string> {
  const iterations = 100_000;
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const subkey = await pbkdf2(password, salt, iterations, "SHA-512", 32);

  const out = new Uint8Array(13 + salt.length + subkey.length);
  out[0] = 0x01;
  const dv = new DataView(out.buffer);
  dv.setUint32(1, 2, false);          // prf = 2 (HMACSHA512)
  dv.setUint32(5, iterations, false);
  dv.setUint32(9, salt.length, false);
  out.set(salt, 13);
  out.set(subkey, 13 + salt.length);
  return bytesToB64(out);
}

// ---------- HS256 JWT ----------

async function hmacKey(secret: string): Promise<CryptoKey> {
  return await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign", "verify"],
  );
}

/** Mint an HS256 JWT with the given claims (exp handled by caller, in seconds). */
export async function mintJwt(claims: Record<string, unknown>, secret: string): Promise<string> {
  const enc = new TextEncoder();
  const header = b64url(enc.encode(JSON.stringify({ alg: "HS256", typ: "JWT" })));
  const payload = b64url(enc.encode(JSON.stringify(claims)));
  const data = `${header}.${payload}`;
  const sig = await crypto.subtle.sign("HMAC", await hmacKey(secret), enc.encode(data));
  return `${data}.${b64url(new Uint8Array(sig))}`;
}

/** Verify an HS256 JWT: signature + exp. Returns claims or null. */
export async function verifyJwt(
  token: string,
  secret: string,
): Promise<Record<string, unknown> | null> {
  const parts = token.split(".");
  if (parts.length !== 3) return null;
  const enc = new TextEncoder();
  let ok: boolean;
  try {
    ok = await crypto.subtle.verify(
      "HMAC",
      await hmacKey(secret),
      b64urlToBytes(parts[2]) as BufferSource,
      enc.encode(`${parts[0]}.${parts[1]}`),
    );
  } catch {
    return null;
  }
  if (!ok) return null;
  try {
    const claims = JSON.parse(new TextDecoder().decode(b64urlToBytes(parts[1])));
    if (typeof claims.exp !== "number" || claims.exp * 1000 < Date.now()) return null;
    return claims;
  } catch {
    return null;
  }
}

export function nowSec(): number {
  return Math.floor(Date.now() / 1000);
}

// ---------- HTTP helpers ----------

export const CORS_HEADERS: Record<string, string> = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
};

export function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { ...CORS_HEADERS, "Content-Type": "application/json" },
  });
}

/** Simple per-key in-memory rate limiter (per isolate — good enough for a small fleet). */
export class RateLimiter {
  private hits = new Map<string, { count: number; resetAt: number }>();
  constructor(private max: number, private windowMs: number) {}

  /** true = blocked */
  blocked(key: string): boolean {
    const h = this.hits.get(key);
    return !!h && h.resetAt > Date.now() && h.count >= this.max;
  }

  fail(key: string) {
    const now = Date.now();
    const h = this.hits.get(key);
    if (!h || h.resetAt <= now) this.hits.set(key, { count: 1, resetAt: now + this.windowMs });
    else h.count++;
  }

  clear(key: string) {
    this.hits.delete(key);
  }
}
