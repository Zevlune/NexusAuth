import test from 'node:test';
import assert from 'node:assert/strict';

import {
  EMAIL_MAX_LENGTH,
  PHONE_NUMBER_MAX_LENGTH,
  normalizePhoneNumber,
  validateOptionalEmail,
  validateOptionalPhoneNumber,
} from './validation.ts';

const blankEmails: unknown[] = [undefined, null, '', '   ', '\t\n  '];
const blankPhoneNumbers: unknown[] = [undefined, null, '', '   ', '\t\n  '];

test('validateOptionalEmail accepts empty and whitespace-only values', () => {
  for (const value of blankEmails) {
    assert.equal(validateOptionalEmail(value), true, `expected ${JSON.stringify(value)} to be valid`);
  }
});

test('validateOptionalEmail rejects non-string values that are not empty', () => {
  for (const value of [0, 42, true, false, {}, [], ['user@example.com']]) {
    assert.equal(validateOptionalEmail(value), false, `expected ${JSON.stringify(value)} to be invalid`);
  }
});

test('validateOptionalEmail accepts plain single mailbox addresses', () => {
  for (const value of [
    'user@example.com',
    'first.last@example.com',
    'user+tag@sub.example.com',
    'USER@EXAMPLE.COM',
    'user_name-1@example.co.uk',
    '  user@example.com  ',
  ]) {
    assert.equal(validateOptionalEmail(value), true, `expected ${value} to be valid`);
  }
});

test('validateOptionalEmail rejects malformed addresses', () => {
  for (const value of [
    'plainaddress',
    'a@b',
    '@example.com',
    'user@',
    'user@@example.com',
    'user cause@example.com',
    'user@exam ple.com',
    'user@example..com',
    'user@.example.com',
    'user@example.com.',
    '.user@example.com',
    'user.@example.com',
    'us..er@example.com',
    'user@example',
  ]) {
    assert.equal(validateOptionalEmail(value), false, `expected ${value} to be invalid`);
  }
});

test('validateOptionalEmail rejects invalid local part punctuation', () => {
  for (const value of [
    '<user>@example.com',
    'user"quote@example.com',
    'user:bracket@example.com',
    'user;semi@example.com',
    'us(er)@example.com',
    'us er@example.com',
  ]) {
    assert.equal(validateOptionalEmail(value), false, `expected ${value} to be invalid`);
  }
});

test('validateOptionalEmail rejects invalid domain label punctuation', () => {
  for (const value of [
    'a@example.<script>',
    'a@ex_ample.com',
    'a@-example.com',
    'a@example-.com',
    'a@ex!ample.com',
    'a@exam ple.com',
  ]) {
    assert.equal(validateOptionalEmail(value), false, `expected ${value} to be invalid`);
  }
});

test('validateOptionalEmail enforces the 256 character limit after trimming', () => {
  const domain = '@example.com';
  const exact = 'a'.repeat(EMAIL_MAX_LENGTH - domain.length) + domain;
  const tooLong = 'a'.repeat(EMAIL_MAX_LENGTH - domain.length + 1) + domain;

  assert.equal(exact.length, EMAIL_MAX_LENGTH);
  assert.equal(tooLong.length, EMAIL_MAX_LENGTH + 1);
  assert.equal(validateOptionalEmail(exact), true);
  assert.equal(validateOptionalEmail(`  ${exact}  `), true);
  assert.equal(validateOptionalEmail(tooLong), false);
});

test('validateOptionalPhoneNumber accepts empty and whitespace-only values', () => {
  for (const value of blankPhoneNumbers) {
    assert.equal(validateOptionalPhoneNumber(value), true, `expected ${JSON.stringify(value)} to be valid`);
  }
});

test('validateOptionalPhoneNumber rejects non-string values that are not empty', () => {
  for (const value of [0, 42, true, {}, ['13800138000']]) {
    assert.equal(validateOptionalPhoneNumber(value), false, `expected ${JSON.stringify(value)} to be invalid`);
  }
});

test('validateOptionalPhoneNumber accepts mainland China mobile numbers', () => {
  for (const value of [
    '13800138000',
    ' 13800138000 ',
    '138 0013 8000',
    '138-0013-8000',
    '19900000000',
    '+8613800138000',
    '+86 138 0013 8000',
    '+86-138-0013-8000',
  ]) {
    assert.equal(validateOptionalPhoneNumber(value), true, `expected ${value} to be valid`);
  }
});

test('validateOptionalPhoneNumber rejects malformed mainland China numbers', () => {
  for (const value of [
    '12800138000',
    '10000138000',
    '1380013800',
    '138001380000',
    '23800138000',
    '010-12345678',
  ]) {
    assert.equal(validateOptionalPhoneNumber(value), false, `expected ${value} to be invalid`);
  }
});

test('validateOptionalPhoneNumber accepts international numbers with a zero-free country code', () => {
  for (const value of [
    '+14155552671',
    '+44 20 7946 0958',
    '+819012345678',
    '+12345678',
    '+123456789012345',
  ]) {
    assert.equal(validateOptionalPhoneNumber(value), true, `expected ${value} to be valid`);
  }
});

test('validateOptionalPhoneNumber rejects malformed international numbers', () => {
  for (const value of [
    '+1234567',
    '+1234567890123456',
    '+0123456789',
    '++8613800138000',
    '++14155552671',
    '+86',
  ]) {
    assert.equal(validateOptionalPhoneNumber(value), false, `expected ${value} to be invalid`);
  }
});

test('validateOptionalPhoneNumber treats a +86 prefix as mainland-only', () => {
  for (const value of [
    '+8610000000000',
    '+8613800',
    '+861380013800',
    '+86138001380000',
    '+8612800138000',
    '+86 1000 0000 0000',
  ]) {
    assert.equal(validateOptionalPhoneNumber(value), false, `expected ${value} to be invalid`);
  }
});

test('validateOptionalPhoneNumber rejects letters and unsupported separators', () => {
  for (const value of [
    'abcdefghijk',
    '13800138000a',
    '(138)0013-8000',
    '138.0013.8000',
    '138/0013/8000',
    '+13800138000abc',
  ]) {
    assert.equal(validateOptionalPhoneNumber(value), false, `expected ${value} to be invalid`);
  }
});

test('validateOptionalPhoneNumber enforces the 20 character raw trimmed limit', () => {
  const atLimit = '+86--138--0013--8000';
  const overLimit = `${atLimit}-`;
  assert.equal(atLimit.length, PHONE_NUMBER_MAX_LENGTH);
  assert.equal(overLimit.length, PHONE_NUMBER_MAX_LENGTH + 1);
  assert.equal(validateOptionalPhoneNumber(`  ${atLimit}  `), true);
  assert.equal(validateOptionalPhoneNumber(`  ${overLimit}  `), false);
});

test('normalizePhoneNumber trims and strips spaces and hyphens', () => {
  assert.equal(normalizePhoneNumber('  138 0013-8000 '), '13800138000');
  assert.equal(normalizePhoneNumber('+86 138-0013-8000'), '+8613800138000');
  assert.equal(normalizePhoneNumber('a-b c'), 'abc');
});

test('normalizePhoneNumber keeps empty strings empty', () => {
  assert.equal(normalizePhoneNumber(''), '');
  assert.equal(normalizePhoneNumber('   '), '');
  assert.equal(normalizePhoneNumber(' - '), '');
});
