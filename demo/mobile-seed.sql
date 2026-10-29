-- WARNING: ISOLATED DEMO DATABASES ONLY.
-- This seed registers a public development client secret. Run it only against
-- a disposable demo database that contains no production, staging, shared, or
-- personal accounts. Existing clients and secrets are preserved.
--
-- Required variables:
--   NEXUSAUTH_ALLOW_DEMO_SEED=1
--   NEXUSAUTH_DEMO_DATABASE=<isolated-demo-database>
--   mobile_secret=<local-demo-secret>
--   mobile_redirect=<exact-local-callback-url>
--
-- Example:
--   psql -v ON_ERROR_STOP=1 \
--     -v NEXUSAUTH_ALLOW_DEMO_SEED=1 \
--     -v NEXUSAUTH_DEMO_DATABASE=nexusauth_demo \
--     -v mobile_secret='replace-with-a-local-demo-secret' \
--     -v mobile_redirect='http://localhost:5401/signin-oidc' \
--     -f demo/mobile-seed.sql

\set ON_ERROR_STOP on

\if :{?NEXUSAUTH_ALLOW_DEMO_SEED}
\else
    \set NEXUSAUTH_ALLOW_DEMO_SEED 0
\endif

\if :{?NEXUSAUTH_DEMO_DATABASE}
\else
    \set NEXUSAUTH_DEMO_DATABASE ''
\endif

SELECT CASE
    WHEN :'NEXUSAUTH_ALLOW_DEMO_SEED' = '1'
     AND :'NEXUSAUTH_DEMO_DATABASE' <> ''
    THEN 1
    ELSE 0
END AS nexusauth_demo_seed_allowed
\gset

\if :nexusauth_demo_seed_allowed
\else
    \echo 'REFUSED: set NEXUSAUTH_ALLOW_DEMO_SEED=1 and NEXUSAUTH_DEMO_DATABASE to an isolated demo database.'
    \quit
\endif

\connect :"NEXUSAUTH_DEMO_DATABASE"
SET search_path TO nexusauth, public;
BEGIN;
INSERT INTO oauth_clients (id, client_id, token_endpoint_auth_method, client_name, description,
    redirect_uris, post_logout_redirect_uris, allowed_scopes, allowed_grant_types, require_pkce, is_active, created_at)
VALUES ('20000000-0000-0000-0000-000000000104', 'demo-mobile-bff', 'client_secret_basic',
    'Demo Mobile BFF Client', 'React Native mobile login broker; credentials stay on the backend.',
    jsonb_build_array(:'mobile_redirect'), '[]', '["openid","profile","email","offline_access"]',
    '["authorization_code","refresh_token"]', true, true, NOW())
ON CONFLICT DO NOTHING;
INSERT INTO oauth_client_secrets (id, client_id, type, value, description, is_active, created_at)
SELECT '21000000-0000-0000-0000-000000000104', id, 'shared_secret',
    crypt(:'mobile_secret', gen_salt('bf', 12)), 'Mobile demo backend only', true, NOW()
FROM oauth_clients WHERE client_id = 'demo-mobile-bff'
ON CONFLICT DO NOTHING;
COMMIT;
