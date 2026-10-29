-- Incremental schema update for refresh token claim constraints.
-- Safe to run against an initialized NexusAuth database.

BEGIN;

ALTER TABLE nexusauth.refresh_tokens
    ADD COLUMN IF NOT EXISTS claims_json text;

COMMIT;
