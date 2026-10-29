-- Incremental schema update for issue #49.
-- Safe to run repeatedly against an initialized NexusAuth database.

BEGIN;

ALTER TABLE nexusauth.oauth_clients
    ADD COLUMN IF NOT EXISTS require_consent boolean;

UPDATE nexusauth.oauth_clients
SET require_consent = false
WHERE require_consent IS NULL;

ALTER TABLE nexusauth.oauth_clients
    ALTER COLUMN require_consent SET DEFAULT false,
    ALTER COLUMN require_consent SET NOT NULL;

COMMIT;
