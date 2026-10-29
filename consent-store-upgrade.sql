-- Independent, idempotent consent persistence upgrade.
-- Apply with:
--   psql "$NEXUSAUTH_CONNECTION_STRING" -v ON_ERROR_STOP=1 -f consent-store-upgrade.sql

CREATE SCHEMA IF NOT EXISTS nexusauth;

CREATE TABLE IF NOT EXISTS nexusauth.consent_requests (
    id                  uuid            NOT NULL,
    request_id_hash     varchar(43)     NOT NULL,
    user_id             uuid            NOT NULL,
    query               text            NOT NULL,
    status              varchar(32)     NOT NULL,
    remember            boolean         NOT NULL,
    created_at          timestamptz     NOT NULL,
    expires_at          timestamptz     NOT NULL,
    approved_at         timestamptz,
    consumed_at         timestamptz,
    CONSTRAINT pk_consent_requests PRIMARY KEY (id),
    CONSTRAINT ck_consent_requests_request_id_hash_base64url
        CHECK (request_id_hash ~ '^[A-Za-z0-9_-]{43}$'),
    CONSTRAINT ck_consent_requests_expiry_after_creation
        CHECK (expires_at > created_at),
    CONSTRAINT ck_consent_requests_status
        CHECK (status IN ('Pending', 'Approved', 'Consumed')),
    CONSTRAINT ck_consent_requests_state_timestamps
        CHECK (
            (status = 'Pending' AND approved_at IS NULL AND consumed_at IS NULL)
            OR (status = 'Approved' AND approved_at IS NOT NULL AND consumed_at IS NULL)
            OR (status = 'Consumed' AND approved_at IS NOT NULL AND consumed_at IS NOT NULL)
        )
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_consent_requests_request_id_hash
    ON nexusauth.consent_requests (request_id_hash);
CREATE INDEX IF NOT EXISTS ix_consent_requests_consume
    ON nexusauth.consent_requests (request_id_hash, user_id, status, expires_at);
CREATE INDEX IF NOT EXISTS ix_consent_requests_user_state
    ON nexusauth.consent_requests (user_id, status, expires_at);
CREATE INDEX IF NOT EXISTS ix_consent_requests_expires_at
    ON nexusauth.consent_requests (expires_at);

CREATE TABLE IF NOT EXISTS nexusauth.consent_grants (
    id                  uuid            NOT NULL,
    user_id             uuid            NOT NULL,
    client_id           varchar(128)    NOT NULL,
    scope_normalized    varchar(512)    NOT NULL,
    claims              text,
    claims_match_key    varchar(65)     NOT NULL,
    created_at          timestamptz     NOT NULL,
    CONSTRAINT pk_consent_grants PRIMARY KEY (id),
    CONSTRAINT ck_consent_grants_scope_not_empty
        CHECK (length(scope_normalized) > 0),
    CONSTRAINT ck_consent_grants_claims_match_key
        CHECK (claims_match_key = 'n' OR claims_match_key ~ '^v:[A-Za-z0-9_-]{43}$'),
    CONSTRAINT ck_consent_grants_claims_key_consistency
        CHECK (
            (claims IS NULL AND claims_match_key = 'n')
            OR (claims IS NOT NULL AND claims_match_key LIKE 'v:%')
        )
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_consent_grants_identity
    ON nexusauth.consent_grants
        (user_id, client_id, scope_normalized, claims_match_key);
CREATE INDEX IF NOT EXISTS ix_consent_grants_user_client
    ON nexusauth.consent_grants (user_id, client_id);
