-- Incremental setup: run against the existing NexusAuth database with psql.
-- AppId/AppSecret are server configuration and must never appear in this script.
BEGIN;

CREATE TABLE IF NOT EXISTS nexusauth.user_external_identities (
    id uuid NOT NULL,
    user_id uuid NOT NULL REFERENCES nexusauth.users(id) ON DELETE CASCADE,
    provider varchar(32) NOT NULL,
    issuer varchar(256) NOT NULL,
    subject varchar(256) NOT NULL,
    union_id varchar(256),
    created_at timestamptz NOT NULL,
    last_authenticated_at timestamptz NOT NULL,
    CONSTRAINT pk_user_external_identities PRIMARY KEY (id),
    CONSTRAINT ck_user_external_identities_authentication_after_creation
        CHECK (last_authenticated_at >= created_at)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_user_external_identities_provider_issuer_subject
    ON nexusauth.user_external_identities (provider, issuer, subject);
CREATE INDEX IF NOT EXISTS ix_user_external_identities_provider_issuer_union_id
    ON nexusauth.user_external_identities (provider, issuer, union_id)
    WHERE union_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_user_external_identities_user_id
    ON nexusauth.user_external_identities (user_id);

-- New installations may also run this after production-init.sql.
-- Existing clients are deliberately left unchanged. Review their method/grants
-- in Workbench; choose a separate public client if the name is already used.
INSERT INTO nexusauth.oauth_clients
    (id, client_id, client_name, token_endpoint_auth_method, redirect_uris,
     post_logout_redirect_uris, allowed_scopes, allowed_grant_types,
     require_pkce, is_active, created_at)
VALUES
    ('78ca78de-2987-4f5c-bb29-e72126162b40', 'wechat-miniapp', '微信小程序', 'none',
     '[]'::jsonb, '[]'::jsonb, '["openid","profile","offline_access"]'::jsonb,
     '["urn:nexusauth:grant-type:wechat_miniprogram","refresh_token"]'::jsonb,
     false, true, CURRENT_TIMESTAMP)
ON CONFLICT (client_id) DO NOTHING;

COMMIT;
