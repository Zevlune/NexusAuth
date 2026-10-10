(() => {
    const initialized = new WeakSet();
    const pending = new WeakSet();
    const fallback = new Map();
    const selector = '[data-email-send]';
    const storagePrefix = 'nexusauth.email.cooldown:';
    function key(button) {
        const email = button.form?.querySelector(button.dataset.emailInput)?.value.trim().toLowerCase();
        return email ? storagePrefix + email : null;
    }
    function read(storageKey) {
        if (!storageKey) return 0;
        try { return Number(localStorage.getItem(storageKey)) || 0; }
        catch (_) { return fallback.get(storageKey) || 0; }
    }
    function remember(storageKey, deadline) {
        if (!storageKey) return;
        fallback.set(storageKey, deadline);
        try { localStorage.setItem(storageKey, String(deadline)); } catch (_) { /* Storage may be disabled. */ }
    }
    function render(button) {
        if (!initialized.has(button)) {
            initialized.add(button);
            button.dataset.sendLabel = button.textContent.trim();
            const seconds = Number(button.dataset.cooldownSeconds) || 0;
            if (seconds > 0) remember(key(button), Math.max(read(key(button)), Date.now() + seconds * 1000));
        }
        const seconds = Math.max(0, Math.ceil((read(key(button)) - Date.now()) / 1000));
        button.disabled = seconds > 0 || pending.has(button);
        button.setAttribute('aria-disabled', String(button.disabled));
        const label = pending.has(button) ? '发送中…' : seconds ? `重新发送（${seconds}s）` : button.dataset.sendLabel;
        if (button.textContent !== label) button.textContent = label;
    }
    function refresh() { document.querySelectorAll(selector).forEach(render); }
    function fieldError(form, name, message) {
        const input = form.querySelector(`[name="${name}"]`);
        const hint = form.querySelector(`[data-email-field-error="${name}"]`);
        if (!input || !hint) return;
        hint.textContent = message;
        input.classList.toggle('field-invalid', Boolean(message));
        input.setAttribute('aria-invalid', String(Boolean(message)));
    }
    function validateEmail(input) {
        const email = input.value.trim();
        const parts = email.split('@');
        const valid = email.length <= 254 && parts.length === 2 && parts[0].length <= 64 &&
            /^[a-zA-Z0-9!#$%&'*+\/=?^_`{|}~-]+(?:\.[a-zA-Z0-9!#$%&'*+\/=?^_`{|}~-]+)*$/.test(parts[0]) &&
            parts[1].includes('.') && parts[1].split('.').every(label =>
                label.length <= 63 && /^[a-zA-Z0-9](?:[a-zA-Z0-9-]*[a-zA-Z0-9])?$/.test(label));
        fieldError(input.form, input.name, !email ? '请输入邮箱地址。' : valid ? '' : '请输入正确的邮箱地址，例如 name@example.com。');
        return valid;
    }
    function status(button, message, success) {
        const hint = button.form.closest('section')?.querySelector('[data-email-send-status]');
        if (!hint) return;
        hint.textContent = message || '';
        hint.classList.toggle('hidden', !message);
        hint.classList.toggle('success', success);
        hint.classList.toggle('error', !success);
    }
    document.addEventListener('submit', event => {
        const button = event.submitter;
        if (!button) return;
        const email = button.matches(selector) && button.form.querySelector(button.dataset.emailInput);
        if (email && !validateEmail(email)) {
            event.preventDefault(); event.stopImmediatePropagation(); email.focus(); return;
        }
        if (button.matches(selector) && (read(key(button)) > Date.now() || pending.has(button))) {
            event.preventDefault(); event.stopImmediatePropagation(); render(button); return;
        }
        const password = button.dataset.passwordInput && button.form.querySelector(button.dataset.passwordInput);
        if (password && !password.value.trim()) {
            event.preventDefault(); event.stopImmediatePropagation();
            fieldError(button.form, password.name, '请输入当前密码。');
            password.focus();
        }
    }, true);
    document.addEventListener('submit', async event => {
        const button = event.submitter;
        // CAPTCHA cancels its first submit. Ordinary page forms start their
        // timer only from the successful server-rendered response.
        if (event.defaultPrevented || !button?.matches(selector) || button.dataset.emailAjax !== 'true') return;
        event.preventDefault();
        const form = button.form;
        const storageKey = key(button);
        const body = new FormData(form);
        const url = button.getAttribute('formaction') || form.action;
        fieldError(form, 'CurrentPassword', '');
        status(button, '', false);
        pending.add(button); render(button);
        try {
            const response = await fetch(url, {
                method: 'POST', body, credentials: 'same-origin',
                headers: { 'X-Email-Code-Request': 'true', 'Accept': 'application/json' }
            });
            if (!response.ok || !response.headers.get('Content-Type')?.includes('application/json'))
                throw new Error('Send request failed');
            const result = await response.json();
            if (result.success === true) {
                const seconds = Number(result.cooldownSeconds);
                if (Number.isFinite(seconds) && seconds > 0) remember(storageKey, Date.now() + seconds * 1000);
                form.querySelector('[name=CurrentPassword]').value = '';
                const otp = form.querySelector('[name=TotpCode]');
                if (otp) otp.value = '';
                status(button, result.message, true);
            } else {
                for (const [name, message] of Object.entries(result.errors || {})) fieldError(form, name, message);
                if (!Object.keys(result.errors || {}).length) status(button, result.message || '发送失败，请稍后重试。', false);
            }
        } catch (_) { status(button, '发送失败，请稍后重试。', false); }
        finally { pending.delete(button); render(button); }
    });
    document.addEventListener('input', event => {
        if (event.target.name === 'CurrentPassword') fieldError(event.target.form, 'CurrentPassword', '');
        if (event.target.matches('input[type=email], input[name=Username]')) {
            fieldError(event.target.form, event.target.name, '');
            refresh();
        }
    });
    document.addEventListener('focusout', event => {
        if (event.target.matches('[data-email-validate]:not([data-email-validate="false"])')) validateEmail(event.target);
    });
    document.addEventListener('invalid', event => {
        if (event.target.matches('[data-email-validate]:not([data-email-validate="false"])')) {
            event.preventDefault(); validateEmail(event.target); event.target.focus();
        }
    }, true);
    window.addEventListener('storage', refresh);
    window.addEventListener('pageshow', refresh);
    new MutationObserver(refresh).observe(document.body, { childList: true, subtree: true });
    setInterval(refresh, 1000);
    refresh();
})();
