-- Mobile demo ONLY: does not change users or existing Web clients.
-- psql -v mobile_secret='your-local-secret' -v mobile_redirect='http://localhost:5401/signin-oidc' -f demo/mobile-seed.sql
\connect nexusauth
SET search_path TO nexusauth, public;
BEGIN;
INSERT INTO oauth_clients (id, client_id, token_endpoint_auth_method, client_name, description,
    redirect_uris, post_logout_redirect_uris, allowed_scopes, allowed_grant_types, require_pkce, is_active, created_at)
VALUES ('20000000-0000-0000-0000-000000000104', 'demo-mobile-bff', 'client_secret_basic',
    'Demo Mobile BFF Client', 'React Native mobile login broker; credentials stay on the backend.',
    jsonb_build_array(:'mobile_redirect'), '[]', '["openid","profile","email","offline_access"]',
    '["authorization_code","refresh_token"]', true, true, NOW())
ON CONFLICT (client_id) DO UPDATE SET
    token_endpoint_auth_method = EXCLUDED.token_endpoint_auth_method,
    redirect_uris = EXCLUDED.redirect_uris, allowed_scopes = EXCLUDED.allowed_scopes,
    allowed_grant_types = EXCLUDED.allowed_grant_types, require_pkce = true, is_active = true;
INSERT INTO oauth_client_secrets (id, client_id, type, value, description, is_active, created_at)
SELECT '21000000-0000-0000-0000-000000000104', id, 'shared_secret',
    crypt(:'mobile_secret', gen_salt('bf', 12)), 'Mobile demo backend only', true, NOW()
FROM oauth_clients WHERE client_id = 'demo-mobile-bff'
ON CONFLICT (id) DO UPDATE SET value = EXCLUDED.value, is_active = true;
COMMIT;
