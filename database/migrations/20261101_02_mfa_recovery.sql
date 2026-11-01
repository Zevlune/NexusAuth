-- MFA recovery codes and audited administrator resets.
-- This is an additive migration for databases already initialized by the
-- repository's production schema.

BEGIN;

CREATE TABLE IF NOT EXISTS nexusauth.user_recovery_codes (
    id          uuid         NOT NULL,
    user_id     uuid         NOT NULL REFERENCES nexusauth.users(id) ON DELETE CASCADE,
    code_hash   varchar(64)  NOT NULL,
    created_at  timestamptz  NOT NULL,
    used_at     timestamptz,
    CONSTRAINT pk_user_recovery_codes PRIMARY KEY (id),
    CONSTRAINT ck_user_recovery_codes_hash_hex
        CHECK (code_hash ~ '^[A-Fa-f0-9]{64}$')
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_user_recovery_codes_user_hash
    ON nexusauth.user_recovery_codes (user_id, code_hash);
CREATE INDEX IF NOT EXISTS ix_user_recovery_codes_user_used
    ON nexusauth.user_recovery_codes (user_id, used_at);

CREATE TABLE IF NOT EXISTS nexusauth.second_factor_recovery_audits (
    id          uuid         NOT NULL,
    actor_id    uuid         NOT NULL,
    user_id     uuid         NOT NULL,
    reason      varchar(500) NOT NULL,
    occurred_at timestamptz  NOT NULL,
    CONSTRAINT pk_second_factor_recovery_audits PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS ix_second_factor_recovery_audits_user_id
    ON nexusauth.second_factor_recovery_audits (user_id);
CREATE INDEX IF NOT EXISTS ix_second_factor_recovery_audits_occurred_at
    ON nexusauth.second_factor_recovery_audits (occurred_at);

COMMIT;
