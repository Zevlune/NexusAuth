-- Incremental schema update for issue #53.
-- Safe to run against an initialized NexusAuth database.

BEGIN;

CREATE INDEX IF NOT EXISTS ix_authorization_codes_expires_at
    ON nexusauth.authorization_codes (expires_at);
CREATE INDEX IF NOT EXISTS ix_refresh_tokens_expires_at
    ON nexusauth.refresh_tokens (expires_at);
CREATE INDEX IF NOT EXISTS ix_device_authorizations_expires_at
    ON nexusauth.device_authorizations (expires_at);
CREATE INDEX IF NOT EXISTS ix_token_blacklist_entries_expires_at
    ON nexusauth.token_blacklist_entries (expires_at);
CREATE INDEX IF NOT EXISTS ix_webauthn_challenges_expires_at
    ON nexusauth.webauthn_challenges (expires_at);
CREATE INDEX IF NOT EXISTS ix_sso_sessions_expires_at
    ON nexusauth.sso_sessions (expires_at);
CREATE INDEX IF NOT EXISTS ix_sso_sessions_revoked_at
    ON nexusauth.sso_sessions (revoked_at);

COMMIT;
