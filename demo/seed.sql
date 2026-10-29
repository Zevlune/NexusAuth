-- ============================================================
-- NexusAuth demo seed data (single source of truth)
--
-- WARNING: ISOLATED DEMO DATABASES ONLY
-- This file contains public demo credentials and JWKS values that match
-- private keys committed under demo/src/*/keys/. The shared client secret
-- is public too. Anyone with repository access can impersonate these demo
-- clients. Run this file only against an isolated, disposable demo database
-- that contains no production, staging, shared, or personal accounts.
--
-- This script refuses to run unless BOTH variables are supplied:
--   NEXUSAUTH_ALLOW_DEMO_SEED=1
--   NEXUSAUTH_DEMO_DATABASE=<isolated-demo-database>
--
-- Existing users, clients, secrets, API resources, and mappings are preserved.
-- New demo users are ordinary active users; this script never grants the
-- system-account flag.
--
-- Example:
--   psql -v ON_ERROR_STOP=1 \
--     -v NEXUSAUTH_ALLOW_DEMO_SEED=1 \
--     -v NEXUSAUTH_DEMO_DATABASE=nexusauth_demo \
--     -f demo/seed.sql
-- ============================================================

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

SET search_path TO nexusauth;

BEGIN;

-- ============================================================
-- API resources with audience mapping
-- requested resource scopes now use audience directly
-- demo-bff-api -> web BFF APIs
-- demo-mobile-api -> mobile bearer APIs
-- ============================================================
INSERT INTO api_resources (id, name, display_name, audience, description, is_active, created_at)
VALUES
    ('10000000-0000-0000-0000-000000000001', 'demo_api', 'Demo API', 'demo-bff-api', 'API scope for web BFF business endpoints', true, NOW()),
    ('10000000-0000-0000-0000-000000000002', 'profile_api', 'Profile API', 'demo-bff-api', 'Profile-related API scope for web BFF', true, NOW()),
    ('10000000-0000-0000-0000-000000000003', 'mobile_api', 'Mobile API', 'demo-mobile-api', 'API scope for mobile bearer endpoints', true, NOW())
ON CONFLICT DO NOTHING;

-- ============================================================
-- OAuth demo clients
-- ============================================================
INSERT INTO oauth_clients (id, client_id, token_endpoint_auth_method, jwks, jwks_uri, client_name, description, redirect_uris, post_logout_redirect_uris, allowed_scopes, allowed_grant_types, require_pkce, is_active, created_at)
VALUES
(
    '20000000-0000-0000-0000-000000000001',
    'demo-bff',
    'private_key_jwt',
    '{"keys":[{"kty":"RSA","kid":"demo-bff-key-1","use":"sig","alg":"RS256","n":"7Bghnu8yZRRAnvax9eqUO0MziC3hSa4XKBP-mEE8WpFs8smd9PLgulFHCJEgZdTpT7eM6i-1HrQl66F2yP9iZiZ8cKbpYzL-QKH8ii8VLaXJ9bMR7mEKPAOU85H3tQS0W5RMGemXdN78o7oQwn8p3_mAoKby77YI3EcwNBaoZo51ud7x7jAxBGA0IiHhTqCrJEo4t3eXREIepEN5xBXAcnTTqTdrUQXWrEL0bD06Hud_xm2SapuxbogLkPKK3keRhTPSbEcZrAbVsNr08WnCMoGa6QsSSpj6Bk4kFF-nJDZyENU0IzZO28n_Uz0bSgEHGlIl0Qe2iv9hz6wdlHw6-Q","e":"AQAB"}]}',
    NULL,
    'Demo Frontend BFF Client',
    'A front-end/back-end separated demo client for authorization code + OIDC',
    '["http://localhost:5201/signin-oidc"]',
    '["http://localhost:5200/"]',
    '["openid","profile","email","phone","offline_access","demo-bff-api"]',
    '["authorization_code","refresh_token"]',
    true,
    true,
    NOW()
),
(
    '20000000-0000-0000-0000-000000000002',
    'demo-bff-secret',
    'client_secret_basic',
    NULL,
    NULL,
    'Demo Frontend BFF Client Secret',
    'A front-end/back-end separated demo client for authorization code + OIDC using client_secret_basic',
    '["http://localhost:5301/signin-oidc"]',
    '["http://localhost:5300/"]',
    '["openid","profile","email","phone","offline_access","demo-bff-api"]',
    '["authorization_code","refresh_token"]',
    true,
    true,
    NOW()
),
(
    '20000000-0000-0000-0000-000000000101',
    'demo-cc',
    'private_key_jwt',
    '{"keys":[{"kty":"RSA","kid":"demo-cc-key-1","use":"sig","alg":"RS256","n":"qcGBVp2tCI3PlfndpcWi5MPT-6sNej-_El3p5ivGDzUy_26Do6R2Bak3o4uJ7a4im5WuHtBRmfzgSJlPl7GPuc5KEjnhIupBtvbpKnEhSFKpcYt-0-E6lIA-asudW2AUgXGWjGO53e9_kml-qZ63E976op-tkecILxDpha846YbHIvpE4_hZLxmw4jCpGSzjvJdnM80po5qguwktBHCQuvUVSk2j_5RwiiN9bKHYNiWoIzpwXNXGyvbaANuk_FYIcHLG-kPHUMuWZSpt7lAq1Z045aiG_Nat9aBAA76klOcyuOF6-FI3qQenkoAEHC-VgG3NsNPRn25CJes0varcdQ","e":"AQAB"}]}',
    NULL,
    'Demo Client Credentials Client',
    'Console demo client for OAuth2 client_credentials grant',
    '[]',
    '[]',
    '["demo-bff-api"]',
    '["client_credentials"]',
    false,
    true,
    NOW()
),
(
    '20000000-0000-0000-0000-000000000102',
    'demo-device',
    'private_key_jwt',
    '{"keys":[{"kty":"RSA","kid":"demo-device-key-1","use":"sig","alg":"RS256","n":"yxfXmgXAnVynvxs0fGwdlmjA71O6tS8O3UuGDZ_7Z0BQ4t_T3fCjIRJeqQ3_-sHG-cocuKm6eCmbinEoneoQVbKZ7HjL_HmWQ2vm2CfpwvtY719R43oHLn1vXxx87kKqDVpCJwETlC-iyA9OkNlbSASY5aFTc5wFd1uxjEUUUwJm9yMn6lclZn23Mt32ayh7j6cfuCWSSI2jeV6v0wikeDm3MYE9G42PZJ9SX-fSsCgY7u3u9OopLgwEUBhnpQT1MEWsxy_hVqNpD-6nVOhhJdw5lka1qEzZJ1Keoq5xib4SmGDGOrEzebd-qq9XMWKNfvZ-BEzCcp1vyHUJ0B7HiQ","e":"AQAB"}]}',
    NULL,
    'Demo Device Flow Client',
    'Console demo client for OAuth2 device_code and refresh_token grants (public-client friendly)',
    '[]',
    '[]',
    '["openid","profile","email","phone","offline_access","demo-bff-api"]',
    '["urn:ietf:params:oauth:grant-type:device_code","refresh_token"]',
    false,
    true,
    NOW()
),
(
    '20000000-0000-0000-0000-000000000103',
    'demo-cert',
    'private_key_jwt',
    '{"keys":[{"kty":"RSA","kid":"demo-cert-key-1","use":"sig","alg":"RS256","n":"pPbIZWa9Kor7p4jaPrNrv29XigyKHTuBU3QG7xDRgy5GqbX16QBl715QtgCqe6mQAYhrazLH7LIecQ1ymeD7OCy6Bm0llgvGSTr8X_JU8dsrVN6MF7ab31bcOy1GggTdXpU-5QTfVcyd0MxQw1zxRubEj4jEhw2abUEn7PAdWgVLyB4r-sz0uMq7DQkUhOI8horrKowP_3rqumuba54Pj4hRwmKoSzEpW3MGvwhaanDzqTSWng00ooOysTNZou198Xpwq-BXxeX868KS-ud4DbebbzOQ85eyToRKC80jJscvlD8QcJJ6Uh_rcJPGJfaZPKgtwLy4q1szxFhXqUepXw","e":"AQAB"}]}',
    NULL,
    'Demo Certificate Client',
    'Console demo client for OAuth2 private_key_jwt token endpoint auth',
    '[]',
    '[]',
    '["demo-bff-api"]',
    '["client_credentials"]',
    false,
    true,
    NOW()
),
(
    '20000000-0000-0000-0000-000000000104',
    'demo-mobile-bff',
    'client_secret_basic',
    NULL,
    NULL,
    'Demo Mobile BFF Client',
    'Mobile app OAuth redirect broker. The secret stays on the backend.',
    '["http://localhost:5401/signin-oidc"]',
    '[]',
    '["openid","profile","email","offline_access"]',
    '["authorization_code","refresh_token"]',
    true,
    true,
    NOW()
)
ON CONFLICT DO NOTHING;

INSERT INTO oauth_client_secrets (id, client_id, type, value, plain_value, description, is_active, created_at)
VALUES
(
    '21000000-0000-0000-0000-000000000002',
    '20000000-0000-0000-0000-000000000002',
    'shared_secret',
    '$2a$12$pw856E1CHH3FfcshE0NwCeETGR5hyYaeudBqZfQYCpXdbBuvOpuuy',
    NULL,
    'demo-bff-secret',
    true,
    NOW()
),
(
    '21000000-0000-0000-0000-000000000104',
    '20000000-0000-0000-0000-000000000104',
    'shared_secret',
    '$2a$12$pw856E1CHH3FfcshE0NwCeETGR5hyYaeudBqZfQYCpXdbBuvOpuuy',
    NULL,
    'demo-mobile-bff (backend only)',
    true,
    NOW()
)
ON CONFLICT DO NOTHING;

-- ============================================================
-- Demo users
-- Public demo password: Pass@123. Existing rows are never modified.
-- ============================================================
INSERT INTO users (id, username, password_hash, email, phone_number, nickname, gender, ethnicity, is_active, created_at, updated_at)
VALUES
(
    '30000000-0000-0000-0000-000000000001',
    'alice',
    '$2a$12$V43kCOSW8gBXQ01do2BW3.a9mIdHOb5Wd1fp5nTMNlBcSpARm0l6S',
    'alice@nexusauth.local',
    '13800000001',
    'Alice Chen',
    2,
    'Han',
    true,
    NOW(),
    NOW()
),
(
    '30000000-0000-0000-0000-000000000002',
    'bob',
    '$2a$12$V43kCOSW8gBXQ01do2BW3.a9mIdHOb5Wd1fp5nTMNlBcSpARm0l6S',
    'bob@nexusauth.local',
    '13800000002',
    'Bob Wang',
    1,
    'Han',
    true,
    NOW(),
    NOW()
),
(
    '30000000-0000-0000-0000-000000000003',
    'admin',
    '$2a$12$V43kCOSW8gBXQ01do2BW3.a9mIdHOb5Wd1fp5nTMNlBcSpARm0l6S',
    'admin@nexusauth.local',
    '13800000003',
    'System Admin',
    1,
    'Han',
    true,
    NOW(),
    NOW()
)
ON CONFLICT DO NOTHING;

-- ============================================================
-- Optional mapping table seed (no foreign keys required)
-- ============================================================
INSERT INTO client_api_resources (client_id, api_resource_id)
VALUES
    ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000001'),
    ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000002'),
    ('20000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000001'),
    ('20000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000002'),
    ('20000000-0000-0000-0000-000000000101', '10000000-0000-0000-0000-000000000001'),
    ('20000000-0000-0000-0000-000000000102', '10000000-0000-0000-0000-000000000001'),
    ('20000000-0000-0000-0000-000000000103', '10000000-0000-0000-0000-000000000001')
ON CONFLICT (client_id, api_resource_id) DO NOTHING;

COMMIT;
