-- Optional cleanup after upgrading all services to 1.0.3 or later.
-- 1.0.3 works whether or not this legacy column exists.
BEGIN;
ALTER TABLE nexusauth.users DROP COLUMN IF EXISTS email_verified_at;
COMMIT;
