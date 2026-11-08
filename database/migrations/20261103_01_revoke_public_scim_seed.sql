-- Revoke the local SCIM integration-test credentials that the removed
-- docker/postgres-init.sh seeded before #70. Their raw tokens are public in
-- git history, and older databases still hold them as active rows that never
-- expire. The Host already refuses these hashes at authentication time; this
-- script makes the stored state (and the Workbench list) agree with that.
--
-- Matching is by token hash only, so a credential that merely reuses the old
-- name with a freshly generated token is left untouched. Safe to re-run.

BEGIN;

UPDATE nexusauth.scim_service_principal_credentials
SET is_active  = false,
    revoked_at = COALESCE(revoked_at, now())
WHERE token_hash IN (
    'UJ7alomHnmklWLdv_dk8nAMaTC3HdDPXYlaRzvt4Dvk', -- local-scim-integration-read
    'J0P-nM9qLuo6idK3Rpd_H7cCmbPU6pV5bOB0QQP-FPE'  -- local-scim-integration-read-write
)
AND (is_active OR revoked_at IS NULL);

COMMIT;
