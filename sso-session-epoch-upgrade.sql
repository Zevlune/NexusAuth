-- Incremental schema update for issue #94.
-- Safe to run against an initialized NexusAuth database.

BEGIN;

ALTER TABLE nexusauth.sso_sessions
    ADD COLUMN IF NOT EXISTS token_invalid_before timestamptz;

COMMIT;
