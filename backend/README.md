# Backend

The server side of RouteSync: edge functions running on Supabase, and the SQL that
shapes the database they talk to. All three clients (`RouteSyncWeb`, `RouteSyncMobile`,
`CameraCountMobile`) reach the fleet through these.

## Layout

| Path | Holds |
|------|-------|
| `supabase/functions/` | Deno edge functions, deployed to Supabase |
| `supabase/functions/_shared/` | JWT signing and verification, audit writer, mail, password rules |
| `schema/` | The SQL applied to the database, in the order it was applied |
| `tools/` | PowerShell probes for checking auth by hand |

The `supabase/` directory keeps that exact name because the CLI looks for it by
convention. Everything else sits beside it.

## Deploying a function

Run from the repository root, naming this directory as the project:

```
npx supabase@latest functions deploy <name> --project-ref vrtluruqaxutecydbrsq --workdir backend --no-verify-jwt
```

`--no-verify-jwt` is right for every function here. Each one verifies its own bearer
token against `JWT_SECRET`, and the two that cannot require a token at all serve callers
who are locked out by definition: `auth-login` signs them in, and
`password-reset-request` mails a code to someone who has forgotten their password.

Secrets live in the Supabase project, not in this repository. A function reads them
through `Deno.env.get`.

## Schema

`schema/` is a record, not a migration runner. Files are applied by hand through the
Supabase SQL editor, so nothing here tracks what the database has already seen.

It is worth keeping because it is the only written form of the authorization model.
`phase7.sql` alone creates the two application roles and the row-level security
policies and grants that decide what each one may read and write; `phase10a.sql` adds
the triggers that keep the audit log append-only. A database dump would recover the
statements, but not the reasoning recorded alongside them.
