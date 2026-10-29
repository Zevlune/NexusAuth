# NexusAuth Demo

The files under `demo/` are for an isolated, disposable demonstration database. Do not import them into production, staging, a shared development database, or a database containing personal accounts.

`seed.sql` registers demo clients whose JWKS values match private keys committed in this repository. It also registers a public shared development secret. Anyone with repository access can impersonate these clients, so the material is safe only in a demo database.

Both seed scripts refuse to run unless both confirmation variables are supplied:

- `NEXUSAUTH_ALLOW_DEMO_SEED=1` explicitly acknowledges that the target is an isolated demo database.
- `NEXUSAUTH_DEMO_DATABASE=<name>` selects that database. Use the actual isolated demo database name; `nexusauth_demo` is only an example.

Create and initialize that separate database first, then point the demo Provider at it. Do not point these commands at a database used by another environment.

Full demo data:

```bash
docker compose exec -T db psql -U nexusauth -d postgres -v ON_ERROR_STOP=1 \
  -v NEXUSAUTH_ALLOW_DEMO_SEED=1 \
  -v NEXUSAUTH_DEMO_DATABASE=nexusauth_demo \
  < demo/seed.sql
```

Mobile client only:

```bash
docker compose exec -T db psql -U nexusauth -d postgres -v ON_ERROR_STOP=1 \
  -v NEXUSAUTH_ALLOW_DEMO_SEED=1 \
  -v NEXUSAUTH_DEMO_DATABASE=nexusauth_demo \
  -v mobile_secret='replace-with-a-local-demo-secret' \
  -v mobile_redirect='http://localhost:5401/signin-oidc' \
  < demo/mobile-seed.sql
```

The scripts insert missing demo rows with `ON CONFLICT DO NOTHING`. Re-running either script does not reset an existing user's password, email, phone number, or active state. Existing OAuth clients, shared secrets, API resources, and client-resource mappings are also preserved. A newly inserted `admin` demo user is an ordinary user and is never granted the system-account flag.

The database name must be configured to match the Provider instance used by the demo applications. The confirmation variable is a deliberate safety gate, so never set it globally or wire it into production deployment commands.
