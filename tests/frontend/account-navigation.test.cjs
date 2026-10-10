const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.resolve(__dirname, '../../src/NexusAuth.Host/wwwroot/js/account-navigation.js'), 'utf8');
const origin = 'https://nexus.test';
const routes = ['/account', '/account/change-password', '/account/two-factor', '/account/passkeys', '/account/login-history'];

class ClassList {
    constructor() { this.values = new Set(); }
    toggle(value, force) {
        if (force === undefined ? !this.values.has(value) : force) this.values.add(value);
        else this.values.delete(value);
    }
    contains(value) { return this.values.has(value); }
}

class Link {
    constructor(path) {
        this.href = new URL(path, origin).href;
        this.pathname = new URL(this.href).pathname;
        this.classList = new ClassList();
        this.attributes = new Map();
        this.target = '';
    }
    hasAttribute(name) { return this.attributes.has(name); }
    setAttribute(name, value) { this.attributes.set(name, value); }
    removeAttribute(name) { this.attributes.delete(name); }
}

class Content {
    constructor(name) {
        this.childNodes = [name];
        this.attributes = new Map();
        this.focusCalls = 0;
    }
    setAttribute(name, value) { this.attributes.set(name, value); }
    removeAttribute(name) { this.attributes.delete(name); }
    replaceChildren(...nodes) { this.childNodes = nodes; }
    focus() { this.focusCalls++; }
}

function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
}

function createHarness() {
    const listeners = new Map();
    const content = new Content('initial page');
    const links = routes.map(route => new Link(route));
    const root = { shellMarker: 'shell stays mounted' };
    const inputs = new Map();
    const assignments = [];
    const fetchCalls = [];
    const historyCalls = [];
    const scrollCalls = [];
    const confirms = [];
    const document = {
        title: 'Initial title',
        querySelector(selector) { return selector === '.account-content' ? content : null; },
        querySelectorAll(selector) { return selector === '.nav-item' ? links : []; },
        addEventListener(type, callback) {
            const callbacks = listeners.get(type) || [];
            callbacks.push(callback);
            listeners.set(type, callbacks);
        },
        getElementById(id) { return inputs.get(id) || null; }
    };
    const location = {
        origin,
        href: `${origin}/account`,
        assigns: assignments,
        assign(url) { assignments.push(url); },
        reloadCalls: 0,
        reload() { this.reloadCalls++; }
    };
    const context = {
        URL,
        AbortController,
        document,
        location,
        history: { pushState(_state, _title, url) { historyCalls.push(url); location.href = url; } },
        window: {
            addEventListener(type, callback) {
                const callbacks = listeners.get(`window:${type}`) || [];
                callbacks.push(callback);
                listeners.set(`window:${type}`, callbacks);
            },
            scrollTo(...args) { scrollCalls.push(args); },
            confirm(message) { confirms.push(message); return context.confirmResult; }
        },
        confirmResult: true,
        fetch(url, options) {
            const call = { url, options };
            fetchCalls.push(call);
            return context.fetchImpl(url, options);
        },
        fetchImpl: async url => responseFor(url),
        DOMParser: class {
            parseFromString(html) {
                return {
                    title: `Title for ${html.route}`,
                    querySelector(selector) {
                        if (html.malformed && selector === '.account-content') return null;
                        if (selector === '.account-content') return { childNodes: [`content:${html.route}`] };
                        if (selector === '.nav-item') return {};
                        return null;
                    }
                };
            }
        }
    };
    vm.runInNewContext(source, context, { filename: 'account-navigation.js' });
    return {
        context, content, document, links, root, inputs, assignments, fetchCalls,
        historyCalls, scrollCalls, confirms,
        emit(type, event, target = 'document') {
            const key = target === 'window' ? `window:${type}` : type;
            for (const callback of listeners.get(key) || []) callback(event);
        }
    };
}

function responseFor(url, options = {}) {
    return {
        ok: options.ok ?? true,
        url: options.url ?? url,
        headers: { get: name => name.toLowerCase() === 'content-type' ? (options.contentType ?? 'text/html; charset=utf-8') : null },
        async text() { return { route: new URL(url).pathname }; }
    };
}

function clickEvent(link) {
    return {
        target: { closest(selector) { return selector === 'a[href]' ? link : null; } },
        button: 0,
        preventDefault() { this.defaultPrevented = true; }
    };
}

test('navigation replaces only account content and updates title and active navigation', async () => {
    const h = createHarness();
    h.emit('click', clickEvent(h.links[1]));
    await new Promise(resolve => setImmediate(resolve));

    assert.deepEqual(h.content.childNodes, ['content:/account/change-password']);
    assert.equal(h.root.shellMarker, 'shell stays mounted');
    assert.equal(h.document.title, 'Title for /account/change-password');
    assert.equal(h.links[1].classList.contains('active'), true);
    assert.equal(h.links[1].attributes.get('aria-current'), 'page');
    assert.equal(h.links[0].classList.contains('active'), false);
    assert.equal(h.historyCalls.length, 1);
    assert.equal(h.content.attributes.has('aria-busy'), false);
});

test('popstate fetches the selected account page without pushing history or reloading', async () => {
    const h = createHarness();
    h.context.location.href = `${origin}/account/passkeys`;
    h.emit('popstate', {} , 'window');
    await new Promise(resolve => setImmediate(resolve));

    assert.equal(h.fetchCalls.length, 1);
    assert.deepEqual(h.content.childNodes, ['content:/account/passkeys']);
    assert.equal(h.historyCalls.length, 0);
    assert.equal(h.context.location.reloadCalls, 0);
});

test('failed, non-HTML, redirected-to-login, and malformed responses fall back to navigation', async t => {
    const cases = [
        ['HTTP error', url => responseFor(url, { ok: false })],
        ['non-HTML', url => responseFor(url, { contentType: 'application/json' })],
        ['authentication redirect', url => responseFor(url, { url: `${origin}/account/login` })],
        ['missing account shell', url => ({ ...responseFor(url), async text() { return { route: new URL(url).pathname, malformed: true }; } })]
    ];
    for (const [name, fetchImpl] of cases) {
        await t.test(name, async () => {
            const h = createHarness();
            h.context.fetchImpl = async url => fetchImpl(url);
            h.emit('click', clickEvent(h.links[2]));
            await new Promise(resolve => setImmediate(resolve));
            assert.equal(h.assignments[0], h.links[2].href);
            assert.deepEqual(h.content.childNodes, ['initial page']);
        });
    }
});

test('rapid navigation aborts the previous request and ignores its late response', async () => {
    const h = createHarness();
    const first = deferred();
    const second = deferred();
    h.context.fetchImpl = (_url, options) => h.fetchCalls.length === 1 ? first.promise : second.promise;

    h.emit('click', clickEvent(h.links[1]));
    h.emit('click', clickEvent(h.links[3]));
    assert.equal(h.fetchCalls[0].options.signal.aborted, true);

    first.resolve(responseFor(h.links[1].href));
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(h.content.childNodes, ['initial page']);

    second.resolve(responseFor(h.links[3].href));
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(h.content.childNodes, ['content:/account/passkeys']);
    assert.equal(h.historyCalls.length, 1);
    assert.equal(h.historyCalls[0], h.links[3].href);
});

test('password toggles and delete confirmations are delegated to document', () => {
    const h = createHarness();
    const input = { type: 'password' };
    h.inputs.set('CurrentPassword', input);
    const toggle = {
        dataset: { passwordToggle: 'CurrentPassword' },
        textContent: '◉',
        attributes: new Map(),
        setAttribute(name, value) { this.attributes.set(name, value); }
    };
    h.emit('click', { target: { closest: selector => selector === '[data-password-toggle]' ? toggle : null } });
    assert.equal(input.type, 'text');
    assert.equal(toggle.attributes.get('aria-label'), '隐藏密码');

    let prevented = false;
    const form = { matches: selector => selector === '[data-confirm-delete]' };
    h.context.confirmResult = false;
    h.emit('submit', { target: form, preventDefault() { prevented = true; } });
    assert.equal(prevented, true);
    assert.equal(h.confirms.length, 1);
});
