# RouteSync Web (admin dashboard)

Server-side Blazor. ASP.NET Core.

## First-time setup (required to run locally)

Phase 7 locked the Supabase `users` table with RLS, so the web server needs the
Supabase **service (secret) key** to read it. That key is **not** in git.

1. Copy the template:
   ```
   cp appsettings.Secret.json.example appsettings.Secret.json
   ```
2. Get the key: Supabase Dashboard -> **Project Settings -> API keys -> secret**
   (`sb_secret_...`).
3. Paste it into `appsettings.Secret.json` (replace `PASTE_SECRET_KEY_HERE`).
4. Run the server (Visual Studio, or `dotnet run`).

`appsettings.Secret.json` is gitignored -> the real key never commits. Every dev who
runs the server locally does this once. In production, set `Supabase:Key` on the host
(env var `Supabase__Key` or host config) instead of the file.

## Who needs the secret key?

| Who | Needs it? |
|---|---|
| Admin/driver logging into the running app | No — email + password only |
| Camera phone | No — fleet bind passcode |
| Dev running THIS web server locally | **Yes** — once, in `appsettings.Secret.json` |
| Production web host | Set once (env var / host config) |

The mobile apps (`RouteSyncMobile`, `CameraCountMobile`) never use the secret key —
they use the publishable key + Supabase edge functions. See `supabase/phase7.sql` and
the edge functions under `supabase/functions/`.
