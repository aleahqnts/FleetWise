# Working on this with someone else

Two different questions, two different answers. Pick per person, per day.

## She only wants to USE the dashboard

Do not clone. Use one running instance and open its URL.

A clone costs a `git pull` for every change, a copy of the service key on her laptop, and
an audit trail that silently records nothing when that key is missing. One instance costs
none of those.

### Quickest, same wifi, no accounts

On your machine:

```bash
dotnet run --project RouteSyncWeb/FleetWise.csproj --urls http://0.0.0.0:5062
```

Find your IP (`ipconfig`, IPv4 under your wifi adapter) and send her
`http://<your-ip>:5062`. Windows will ask to allow the port through the firewall the first
time; allow it for Private networks only.

Only works while your machine is on and both of you are on the same wifi.

### Proper, survives your laptop closing

`RouteSyncWeb/Dockerfile` builds a self-contained image. Any container host takes it
(Render, Fly, Azure Container Apps all have a free tier).

Set `Supabase__Key` in the host's environment panel. The key stays on the server and never
touches her laptop. `.dockerignore` keeps `appsettings.Secret.json` out of the image.

## She is WRITING code

Then she needs the clone, and there is no way around pulling. Git only moves when the
receiving side asks.

Her, whenever she starts work:

```bash
git pull
```

Her, once, ever:

Copy `RouteSyncWeb/appsettings.Secret.json.example` to `RouteSyncWeb/appsettings.Secret.json`
and paste the real secret key. The file is gitignored, so it will never arrive by pull.

**Send that key by hand.** USB, password manager, or read it out. Not chat, not email. It is
the `service_role` key: it bypasses row-level security on the entire database.

If she skips it the app now refuses to start and names the file. It used to boot on the
publishable key and answer "Invalid email or password" to a correct password, which points
at everything except the real cause.

## Neither of you can push into the other's folder

Worth stating plainly, because it is the usual expectation. `git push` uploads to GitHub.
Her working copy changes when she pulls, and at no other moment. Anything that looks like
automatic syncing is something running on her machine and asking on a timer.
