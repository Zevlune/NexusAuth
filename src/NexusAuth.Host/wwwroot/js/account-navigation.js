(() => {
    const content = document.querySelector('.account-content');
    const links = Array.from(document.querySelectorAll('.nav-item'));
    const paths = new Set(links.map(link => new URL(link.href).pathname));
    let pending;

    function isAccountPage(url) {
        return url.origin === location.origin && paths.has(url.pathname) && !url.search && !url.hash;
    }

    async function navigate(url, addHistory) {
        pending?.abort();
        const request = new AbortController();
        pending = request;
        content.setAttribute('aria-busy', 'true');
        try {
            // Keep the current page visible until the next authenticated page is ready.
            const response = await fetch(url.href, {
                credentials: 'same-origin', cache: 'no-store', signal: request.signal
            });
            const destination = new URL(response.url);
            if (!response.ok || !isAccountPage(destination) ||
                !response.headers.get('Content-Type')?.includes('text/html')) {
                location.assign(url.href);
                return;
            }
            const page = new DOMParser().parseFromString(await response.text(), 'text/html');
            const nextContent = page.querySelector('.account-content');
            if (!nextContent || !page.querySelector('.nav-item')) {
                location.assign(url.href);
                return;
            }
            if (request.signal.aborted) return;
            // Only trusted server-rendered account content is adopted; scripts are not re-executed.
            content.replaceChildren(...nextContent.childNodes);
            document.title = page.title;
            links.forEach(link => {
                const active = new URL(link.href).pathname === destination.pathname;
                link.classList.toggle('active', active);
                if (active) link.setAttribute('aria-current', 'page');
                else link.removeAttribute('aria-current');
            });
            if (addHistory) history.pushState(null, '', destination.href);
            content.focus({ preventScroll: true });
            window.scrollTo(0, 0);
        } catch (error) {
            if (!request.signal.aborted) location.assign(url.href);
        } finally {
            if (pending === request) {
                pending = null;
                content.removeAttribute('aria-busy');
            }
        }
    }

    document.addEventListener('click', event => {
        const passwordToggle = event.target.closest('[data-password-toggle]');
        if (passwordToggle) {
            const input = document.getElementById(passwordToggle.dataset.passwordToggle);
            if (!input) return;
            const showing = input.type === 'text';
            input.type = showing ? 'password' : 'text';
            passwordToggle.textContent = showing ? '◉' : '◌';
            passwordToggle.setAttribute('aria-label', showing ? '显示密码' : '隐藏密码');
            return;
        }
        const link = event.target.closest('a[href]');
        if (!link || event.defaultPrevented || event.button !== 0 || event.ctrlKey ||
            event.metaKey || event.shiftKey || event.altKey || link.hasAttribute('download') ||
            (link.target && link.target !== '_self')) return;
        const url = new URL(link.href);
        if (!isAccountPage(url)) return;
        event.preventDefault();
        if (url.href !== location.href || pending) navigate(url, true);
    });

    // Delegation also covers forms and buttons introduced by subsequent navigation.
    document.addEventListener('submit', event => {
        if (event.target.matches('[data-confirm-delete]') &&
            !window.confirm('删除此 Passkey 并退出所有设备？你仍可使用密码和已绑定的身份验证器登录。')) {
            event.preventDefault();
        }
    });

    window.addEventListener('popstate', () => {
        const url = new URL(location.href);
        if (isAccountPage(url)) navigate(url, false);
        else location.reload();
    });
})();
