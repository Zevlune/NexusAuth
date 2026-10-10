const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.resolve(__dirname, '../../src/NexusAuth.Host/wwwroot/js/email-code.js'), 'utf8');

function createHarness({ now = 1_800_000_000_000, sendCooldown = 60, cooldownSeconds = 0, email = 'User@example.test', storage = new Map(), ajax = false, password = 'secret' } = {}) {
    const clock = { now };
    const listeners = new Map();
    const intervals = [];
    const input = { name: email.includes('@') || email === '' ? 'EmailAddress' : 'Username', value: email, attributes: new Map(), classList: classList(), focused: false,
        setAttribute(key, item) { this.attributes.set(key, item); },
        focus() { this.focused = true; },
        matches: selector => selector === 'input[type=email], input[name=Username]' ||
            selector === '[data-email-validate]:not([data-email-validate="false"])' };
    const currentPassword = field('CurrentPassword', password);
    const otp = field('TotpCode', '123456');
    const errors = new Map([['CurrentPassword', { textContent: '' }], ['EmailAddress', { textContent: '' }], ['Username', { textContent: '' }]]);
    const status = { textContent: '', classList: classList() };
    const fields = new Map([[input.name, input], ['CurrentPassword', currentPassword], ['TotpCode', otp]]);
    const form = {
        action: '/account/send',
        querySelector(selector) {
            if (selector.startsWith('[data-email-field-error=')) return errors.get(selector.includes('EmailAddress') ? 'EmailAddress' : selector.includes('Username') ? 'Username' : 'CurrentPassword');
            const name = selector.match(/name=["']?([^\]"']+)/)?.[1];
            return fields.get(name) || (selector === '[name=EmailAddress]' || selector === '[name=Username]' ? input : null);
        },
        closest(selector) { return selector === 'section' ? section : null; }
    };
    const section = { querySelector: selector => selector === '[data-email-send-status]' ? status : null };
    for (const value of fields.values()) value.form = form;
    const button = {
        form,
        dataset: { emailInput: input.name === 'Username' ? '[name=Username]' : '[name=EmailAddress]', sendCooldown: String(sendCooldown), cooldownSeconds: String(cooldownSeconds), emailAjax: String(ajax), passwordInput: ajax ? '[name=CurrentPassword]' : '' },
        textContent: '发送验证码', disabled: false, attributes: new Map(),
        matches: selector => selector === '[data-email-send]',
        getAttribute(name) { return name === 'formaction' ? null : null; },
        setAttribute(name, value) { this.attributes.set(name, value); }
    };
    const document = {
        body: {},
        addEventListener(type, callback, capture = false) {
            const entries = listeners.get(type) || [];
            entries.push({ callback, capture });
            listeners.set(type, entries);
        },
        querySelectorAll(selector) { return selector === '[data-email-send]' ? [button] : []; }
    };
    const windowListeners = new Map();
    class TestDate extends Date { static now() { return clock.now; } }
    class Observer { constructor(callback) { this.callback = callback; } observe() {} }
    let fetchImpl = async () => { throw new Error('fetch not configured'); };
    const context = {
        Date: TestDate, document,
        window: { addEventListener(type, callback) { const entries = windowListeners.get(type) || []; entries.push(callback); windowListeners.set(type, entries); } },
        localStorage: { getItem(key) { return storage.has(key) ? storage.get(key) : null; }, setItem(key, value) { storage.set(key, value); } },
        MutationObserver: Observer,
        FormData: class { constructor(value) { this.form = value; } },
        fetch(...args) { return fetchImpl(...args); },
        setInterval(callback, milliseconds) { intervals.push({ callback, milliseconds }); return intervals.length; },
        queueMicrotask(callback) { callback(); }
    };
    vm.runInNewContext(source, context, { filename: 'email-code.js' });
    return {
        button, input, currentPassword, otp, errors, status, clock, storage, intervals, listeners, windowListeners,
        setFetch(fn) { fetchImpl = fn; },
        keyFor(value = input.value) { return `nexusauth.email.cooldown:${value.trim().toLowerCase()}`; },
        async runSubmit({ captchaPrevent = false } = {}) {
            const event = { submitter: button, target: form, defaultPrevented: false, immediateStopped: false,
                preventDefault() { this.defaultPrevented = true; }, stopImmediatePropagation() { this.immediateStopped = true; } };
            for (const entry of listeners.get('submit').filter(item => item.capture)) {
                entry.callback(event);
                if (event.immediateStopped) break;
            }
            if (!event.immediateStopped && captchaPrevent) event.preventDefault();
            const pending = [];
            if (!event.immediateStopped) {
                for (const entry of listeners.get('submit').filter(item => !item.capture)) pending.push(entry.callback(event));
            }
            await Promise.all(pending);
            return event;
        },
        runInput() { for (const entry of listeners.get('input')) entry.callback({ target: input }); },
        runFocusout() { for (const entry of listeners.get('focusout')) entry.callback({ target: input }); },
        tick(milliseconds) { clock.now += milliseconds; for (const interval of intervals) interval.callback(); },
        pageshow() { for (const callback of windowListeners.get('pageshow') || []) callback(); }
    };
}

function field(name, value) {
    return { name, value, attributes: new Map(), classList: classList(), focused: false,
        setAttribute(key, item) { this.attributes.set(key, item); }, focus() { this.focused = true; } };
}

function classList() {
    const names = new Set();
    return { toggle(name, enabled) { if (enabled) names.add(name); else names.delete(name); }, contains(name) { return names.has(name); } };
}

test('server-rendered success page seeds a 60 second cooldown', () => {
    const h = createHarness({ cooldownSeconds: 60 });
    assert.equal(h.button.disabled, true);
    assert.equal(h.button.textContent, '重新发送（60s）');
    assert.equal(Number(h.storage.get(h.keyFor())) - h.clock.now, 60_000);
});

test('server-rendered success page supports a configured 120 second cooldown', () => {
    const h = createHarness({ cooldownSeconds: 120 });
    assert.equal(h.button.textContent, '重新发送（120s）');
    h.tick(60_000);
    assert.equal(h.button.textContent, '重新发送（60s）');
    h.tick(60_000);
    assert.equal(h.button.disabled, false);
});

test('ordinary form submit does not start a cooldown before the server confirms success', async () => {
    const h = createHarness({ sendCooldown: 60 });
    const event = await h.runSubmit();
    assert.equal(event.defaultPrevented, false);
    assert.equal(h.button.disabled, false);
    assert.equal(h.storage.has(h.keyFor()), false);
});

test('reload restores an existing server-confirmed deadline', () => {
    const storage = new Map();
    const first = createHarness({ cooldownSeconds: 60, storage });
    const reload = createHarness({ now: first.clock.now + 15_000, storage });
    assert.equal(reload.button.textContent, '重新发送（45s）');
    reload.tick(45_000);
    assert.equal(reload.button.disabled, false);
});

test('captcha prevention does not start a countdown', async () => {
    const h = createHarness();
    const event = await h.runSubmit({ captchaPrevent: true });
    assert.equal(event.defaultPrevented, true);
    assert.equal(h.button.disabled, false);
    assert.equal(h.storage.has(h.keyFor()), false);
});

test('AJAX success starts cooldown and clears password and authenticator code', async () => {
    const h = createHarness({ ajax: true });
    h.setFetch(async (url, options) => {
        assert.equal(url, '/account/send');
        assert.equal(options.method, 'POST');
        assert.equal(options.credentials, 'same-origin');
        assert.equal(options.headers['X-Email-Code-Request'], 'true');
        return jsonResponse({ success: true, cooldownSeconds: 120, message: '验证码已发送。' });
    });
    const event = await h.runSubmit();
    assert.equal(event.defaultPrevented, true);
    assert.equal(Number(h.storage.get(h.keyFor())) - h.clock.now, 120_000);
    assert.equal(h.button.textContent, '重新发送（120s）');
    assert.equal(h.currentPassword.value, '');
    assert.equal(h.otp.value, '');
    assert.equal(h.status.textContent, '验证码已发送。');
    assert.equal(h.status.classList.contains('success'), true);
});

test('AJAX business and network failures do not create a cooldown', async t => {
    await t.test('business failure displays validation errors', async () => {
        const h = createHarness({ ajax: true });
        h.setFetch(async () => jsonResponse({ success: false, errors: { CurrentPassword: '密码错误。' } }));
        await h.runSubmit();
        assert.equal(h.storage.has(h.keyFor()), false);
        assert.equal(h.errors.get('CurrentPassword').textContent, '密码错误。');
        assert.equal(h.currentPassword.classList.contains('field-invalid'), true);
    });
    await t.test('network failure displays a retry message', async () => {
        const h = createHarness({ ajax: true });
        h.setFetch(async () => { throw new Error('offline'); });
        await h.runSubmit();
        assert.equal(h.storage.has(h.keyFor()), false);
        assert.equal(h.status.textContent, '发送失败，请稍后重试。');
        assert.equal(h.status.classList.contains('error'), true);
    });
});

test('AJAX request blocks concurrent submit while pending and during cooldown', async () => {
    const h = createHarness({ ajax: true });
    let resolveFetch;
    let calls = 0;
    h.setFetch(() => { calls++; return new Promise(resolve => { resolveFetch = resolve; }); });
    const first = h.runSubmit();
    assert.equal(h.button.disabled, true);
    const duplicate = await h.runSubmit();
    assert.equal(duplicate.defaultPrevented, true);
    assert.equal(calls, 1);
    resolveFetch(jsonResponse({ success: true, cooldownSeconds: 60 }));
    await first;
    const cooledDown = await h.runSubmit();
    assert.equal(cooledDown.immediateStopped, true);
    assert.equal(calls, 1);
});

test('AJAX send requires a nonblank current password', async () => {
    const h = createHarness({ ajax: true, password: '   ' });
    const event = await h.runSubmit();
    assert.equal(event.defaultPrevented, true);
    assert.equal(event.immediateStopped, true);
    assert.equal(h.currentPassword.focused, true);
    assert.equal(h.errors.get('CurrentPassword').textContent, '请输入当前密码。');
});

test('email changes isolate stored cooldowns and returning to an address restores its timer', () => {
    const h = createHarness({ cooldownSeconds: 120 });
    const deadline = h.storage.get(h.keyFor());
    h.input.value = 'Other@example.test';
    h.runInput();
    assert.equal(h.button.disabled, false);
    h.input.value = 'USER@example.test';
    h.runInput();
    assert.equal(h.button.disabled, true);
    assert.equal(h.storage.get(h.keyFor()), deadline);
});

test('email validation shows empty and malformed address hints on focusout and clears them on input', () => {
    const h = createHarness({ email: '' });
    h.runFocusout();
    assert.equal(h.errors.get(h.input.name).textContent, '请输入邮箱地址。');
    assert.equal(h.input.classList.contains('field-invalid'), true);

    h.input.value = 'invalid@@example.com';
    h.runFocusout();
    assert.equal(h.errors.get(h.input.name).textContent, '请输入正确的邮箱地址，例如 name@example.com。');
    h.runInput();
    assert.equal(h.errors.get(h.input.name).textContent, '');
    assert.equal(h.input.classList.contains('field-invalid'), false);
});

test('invalid email formats prevent send, focus the email field, and do not fetch or start cooldown', async t => {
    for (const email of ['', 'plainaddress', 'a@@example.com', 'name@example', 'first..last@example.com', 'name@-example.com', 'name@example-.com', 'name@example..com']) {
        await t.test(JSON.stringify(email), async () => {
            const h = createHarness({ email, ajax: true });
            let fetches = 0;
            h.setFetch(async () => { fetches++; return jsonResponse({ success: true, cooldownSeconds: 60 }); });

            const event = await h.runSubmit();

            assert.equal(event.defaultPrevented, true);
            assert.equal(event.immediateStopped, true);
            assert.equal(h.input.focused, true);
            assert.equal(fetches, 0);
            assert.equal(h.storage.has(h.keyFor()), false);
        });
    }
});

test('plus-address and subdomain email formats can be sent', async () => {
    const email = 'alice+security@example.mail.test';
    const h = createHarness({ email, ajax: true });
    let fetches = 0;
    h.setFetch(async () => { fetches++; return jsonResponse({ success: true, cooldownSeconds: 60 }); });

    const event = await h.runSubmit();

    assert.equal(event.defaultPrevented, true);
    assert.equal(event.immediateStopped, false);
    assert.equal(fetches, 1);
    assert.equal(Number(h.storage.get(h.keyFor())) - h.clock.now, 60_000);
});

function jsonResponse(value) {
    return { ok: true, headers: { get: name => name === 'Content-Type' ? 'application/json; charset=utf-8' : null }, json: async () => value };
}
