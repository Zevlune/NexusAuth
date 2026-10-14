export const EMAIL_MAX_LENGTH = 256;
export const PHONE_NUMBER_MAX_LENGTH = 20;

const MAINLAND_MOBILE_PATTERN = /^1[3-9]\d{9}$/;
const PHONE_NUMBER_ALLOWED_PATTERN = /^\+?[\d\s-]+$/;
const DIGITS_ONLY_PATTERN = /^\d+$/;
// Ordinary unquoted local part: atext characters with single "." separators.
const EMAIL_LOCAL_PART_PATTERN =
  /^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*$/;
// Domain label: alphanumeric with internal hyphens, no leading/trailing hyphen.
const EMAIL_DOMAIN_LABEL_PATTERN = /^[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?$/;

/**
 * Optional-field friendly email check for TDesign form rules.
 * Empty, whitespace-only, null and undefined values are treated as "not provided".
 */
export function validateOptionalEmail(value: unknown): boolean {
  if (value === null || value === undefined) {
    return true;
  }

  if (typeof value !== 'string') {
    return false;
  }

  const trimmed = value.trim();
  if (trimmed.length === 0) {
    return true;
  }

  if (trimmed.length > EMAIL_MAX_LENGTH) {
    return false;
  }

  // Whitespace anywhere else means it is not a single mailbox.
  if (/\s/.test(trimmed)) {
    return false;
  }

  const segments = trimmed.split('@');
  if (segments.length !== 2) {
    return false;
  }

  const [local, domain] = segments;
  if (local.length === 0 || domain.length === 0) {
    return false;
  }

  if (!EMAIL_LOCAL_PART_PATTERN.test(local)) {
    return false;
  }

  if (!domain.includes('.')) {
    return false;
  }

  if (!domain.split('.').every((label) => EMAIL_DOMAIN_LABEL_PATTERN.test(label))) {
    return false;
  }

  return true;
}

/**
 * Optional-field friendly mainland-China / international mobile check.
 * Empty, whitespace-only, null and undefined values are treated as "not provided".
 */
export function validateOptionalPhoneNumber(value: unknown): boolean {
  if (value === null || value === undefined) {
    return true;
  }

  if (typeof value !== 'string') {
    return false;
  }

  const trimmed = value.trim();
  if (trimmed.length === 0) {
    return true;
  }

  if (trimmed.length > PHONE_NUMBER_MAX_LENGTH) {
    return false;
  }

  // Only digits, spaces, hyphens and a single leading "+" are accepted.
  if (!PHONE_NUMBER_ALLOWED_PATTERN.test(trimmed)) {
    return false;
  }

  const normalized = normalizePhoneNumber(trimmed);
  const hasLeadingPlus = normalized.startsWith('+');
  const digits = hasLeadingPlus ? normalized.slice(1) : normalized;

  if (!DIGITS_ONLY_PATTERN.test(digits)) {
    return false;
  }

  if (!hasLeadingPlus) {
    return MAINLAND_MOBILE_PATTERN.test(digits);
  }

  // A "+86" prefix is always treated as mainland China: "+86" plus exactly 11 digits
  // in mainland mobile format. No fallback to the generic international rule.
  if (digits.startsWith('86')) {
    return digits.length === 13 && MAINLAND_MOBILE_PATTERN.test(digits.slice(2));
  }

  // Other international numbers: non-zero country code, 8-15 digits in total.
  if (digits.length < 8 || digits.length > 15) {
    return false;
  }

  return !digits.startsWith('0');
}

/**
 * Trims the value and strips spaces and hyphens, keeping an empty string empty.
 */
export function normalizePhoneNumber(value: string): string {
  return value.trim().replace(/[\s-]/g, '');
}
