-- NexusAuth resource lifecycle integrity upgrade.
-- Run with psql -v ON_ERROR_STOP=1. This migration is intentionally fail-fast:
-- duplicate audiences and orphaned references require manual data repair.

\set ON_ERROR_STOP on

BEGIN;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM nexusauth.api_resources
        GROUP BY audience
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION
            'Resource integrity migration stopped: duplicate api_resources.audience values exist; repair them manually before retrying.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM nexusauth.oauth_client_secrets secret
        LEFT JOIN nexusauth.oauth_clients client ON client.id = secret.client_id
        WHERE client.id IS NULL
    ) THEN
        RAISE EXCEPTION
            'Resource integrity migration stopped: orphaned oauth_client_secrets.client_id rows exist; repair them manually before retrying.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM nexusauth.client_api_resources association
        LEFT JOIN nexusauth.oauth_clients client ON client.id = association.client_id
        WHERE client.id IS NULL
    ) THEN
        RAISE EXCEPTION
            'Resource integrity migration stopped: orphaned client_api_resources.client_id rows exist; repair them manually before retrying.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM nexusauth.client_api_resources association
        LEFT JOIN nexusauth.api_resources resource ON resource.id = association.api_resource_id
        WHERE resource.id IS NULL
    ) THEN
        RAISE EXCEPTION
            'Resource integrity migration stopped: orphaned client_api_resources.api_resource_id rows exist; repair them manually before retrying.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM nexusauth.authorization_codes code
        LEFT JOIN nexusauth.oauth_clients client ON client.client_id = code.client_id
        WHERE client.id IS NULL
    ) THEN
        RAISE EXCEPTION
            'Resource integrity migration stopped: orphaned authorization_codes.client_id rows exist; repair them manually before retrying.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM nexusauth.refresh_tokens token
        LEFT JOIN nexusauth.oauth_clients client ON client.client_id = token.client_id
        WHERE client.id IS NULL
    ) THEN
        RAISE EXCEPTION
            'Resource integrity migration stopped: orphaned refresh_tokens.client_id rows exist; repair them manually before retrying.';
    END IF;
END $$;

ALTER TABLE nexusauth.oauth_clients
    ADD COLUMN IF NOT EXISTS is_system_managed boolean NOT NULL DEFAULT false;

ALTER TABLE nexusauth.api_resources
    ADD COLUMN IF NOT EXISTS is_system_managed boolean NOT NULL DEFAULT false;

CREATE UNIQUE INDEX IF NOT EXISTS ix_api_resources_audience
    ON nexusauth.api_resources (audience);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_oauth_client_secrets_client'
          AND conrelid = 'nexusauth.oauth_client_secrets'::regclass
    ) THEN
        ALTER TABLE nexusauth.oauth_client_secrets
            ADD CONSTRAINT fk_oauth_client_secrets_client
            FOREIGN KEY (client_id)
            REFERENCES nexusauth.oauth_clients (id)
            ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_client_api_resources_client'
          AND conrelid = 'nexusauth.client_api_resources'::regclass
    ) THEN
        ALTER TABLE nexusauth.client_api_resources
            ADD CONSTRAINT fk_client_api_resources_client
            FOREIGN KEY (client_id)
            REFERENCES nexusauth.oauth_clients (id)
            ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_client_api_resources_api_resource'
          AND conrelid = 'nexusauth.client_api_resources'::regclass
    ) THEN
        ALTER TABLE nexusauth.client_api_resources
            ADD CONSTRAINT fk_client_api_resources_api_resource
            FOREIGN KEY (api_resource_id)
            REFERENCES nexusauth.api_resources (id)
            ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_authorization_codes_client'
          AND conrelid = 'nexusauth.authorization_codes'::regclass
    ) THEN
        ALTER TABLE nexusauth.authorization_codes
            ADD CONSTRAINT fk_authorization_codes_client
            FOREIGN KEY (client_id)
            REFERENCES nexusauth.oauth_clients (client_id)
            ON DELETE CASCADE;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_refresh_tokens_client'
          AND conrelid = 'nexusauth.refresh_tokens'::regclass
    ) THEN
        ALTER TABLE nexusauth.refresh_tokens
            ADD CONSTRAINT fk_refresh_tokens_client
            FOREIGN KEY (client_id)
            REFERENCES nexusauth.oauth_clients (client_id)
            ON DELETE CASCADE;
    END IF;
END $$;

-- Preserve existing client authorization where a resource audience appears
-- in allowed_scopes but the join row was never written. A client may access
-- several resources, while each token still requests only one audience.
WITH resource_scopes AS (
    SELECT client.id AS client_id, resource.id AS resource_id
    FROM nexusauth.oauth_clients client
    CROSS JOIN LATERAL jsonb_array_elements_text(
        CASE
            WHEN jsonb_typeof(client.allowed_scopes) = 'array' THEN client.allowed_scopes
            ELSE '[]'::jsonb
        END) scope(value)
    JOIN nexusauth.api_resources resource ON resource.audience = scope.value
    GROUP BY client.id, resource.id
)
INSERT INTO nexusauth.client_api_resources (client_id, api_resource_id)
SELECT client_id, resource_id
FROM resource_scopes
ON CONFLICT (client_id, api_resource_id) DO NOTHING;

COMMIT;
