(() => {
    const dialog = document.getElementById('slider-captcha-dialog');
    const form = document.getElementById('login-form') || document.getElementById('register-form');
    if (!dialog || !form) return;
    document.body.appendChild(dialog);
    const scene = document.getElementById('slider-captcha-scene');
    const image = scene.querySelector('.captcha-image');
    const piece = document.getElementById('slider-captcha-piece');
    const track = document.getElementById('slider-captcha-track');
    const handle = document.getElementById('slider-captcha-handle');
    const progress = document.getElementById('slider-captcha-progress');
    const offsetInput = document.getElementById('slider-captcha-offset');
    const tokenInput = document.getElementById('slider-captcha-token');
    const status = document.getElementById('slider-captcha-status');
    const refresh = document.getElementById('slider-captcha-refresh');
    let verified = false, dragging = false, startX = 0, currentOffset = 0;
    let trackTravel = 0, sceneTravel = 0, animationFrame = 0, pendingOffset = 0;
    let returnFocus, submitter;

    function measure() {
        piece.style.width = `${Number(piece.dataset.size) / Number(piece.dataset.width) * 100}%`;
        piece.style.height = `${Number(piece.dataset.size) / Number(piece.dataset.height) * 100}%`;
        piece.style.top = `${Number(piece.dataset.top) / Number(piece.dataset.height) * 100}%`;
        scene.style.aspectRatio = `${piece.dataset.width}/${piece.dataset.height}`;
        trackTravel = Math.max(0, track.clientWidth - handle.offsetWidth);
        sceneTravel = Math.max(0, scene.clientWidth - piece.clientWidth);
        progress.style.width = `${track.clientWidth}px`;
    }
    function setPosition(offset) {
        currentOffset = Math.max(0, Math.min(trackTravel, offset));
        const ratio = trackTravel ? currentOffset / trackTravel : 0;
        handle.style.transform = `translate3d(${currentOffset}px,0,0)`;
        piece.style.transform = `translate3d(${ratio * sceneTravel}px,0,0)`;
        progress.style.transform = `scaleX(${(currentOffset + handle.offsetWidth) / Math.max(1, track.clientWidth)})`;
        offsetInput.value = Math.round(ratio * Number(scene.dataset.trackWidth));
    }
    function reset() {
        if (animationFrame) cancelAnimationFrame(animationFrame);
        animationFrame = 0;
        dragging = false; verified = false;
        measure(); setPosition(0); offsetInput.value = '';
    }
    function close() {
        dialog.classList.add('hidden');
        reset(); returnFocus?.focus();
    }
    form.addEventListener('submit', event => {
        if (verified) return;
        event.preventDefault(); returnFocus = document.activeElement; submitter = event.submitter;
        dialog.classList.remove('hidden'); status.textContent = '';
        reset(); handle.focus();
    });
    refresh.addEventListener('click', async () => {
        if (refresh.disabled) return;
        reset(); refresh.disabled = true; handle.disabled = true;
        status.textContent = '正在换图…';
        try {
            const url = new URL(location.href);
            url.searchParams.set('handler', 'Captcha');
            const response = await fetch(url, { credentials:'same-origin', cache:'no-store' });
            if (!response.ok) throw new Error('Challenge unavailable');
            const challenge = await response.json();
            image.src = challenge.imageDataUrl; piece.src = challenge.pieceDataUrl;
            tokenInput.value = challenge.token;
            Object.assign(piece.dataset, { size:challenge.pieceSize, top:challenge.pieceTop, width:challenge.width, height:challenge.height });
            reset(); status.textContent = '已更换图片，请重新拖动';
        } catch (_) {
            status.textContent = '换图失败，请稍后重试';
        } finally { refresh.disabled = false; handle.disabled = false; }
    });
    handle.addEventListener('pointerdown', event => {
        if (event.button !== 0 || refresh.disabled || verified) return;
        dragging = true; startX = event.clientX - currentOffset;
        handle.setPointerCapture(event.pointerId);
    });
    handle.addEventListener('pointermove', event => {
        if (!dragging) return;
        pendingOffset = event.clientX - startX;
        if (!animationFrame) animationFrame = requestAnimationFrame(() => {
            animationFrame = 0; setPosition(pendingOffset);
        });
    });
    handle.addEventListener('pointerup', event => {
        if (!dragging) return;
        dragging = false; handle.releasePointerCapture(event.pointerId);
        if (animationFrame) cancelAnimationFrame(animationFrame);
        animationFrame = 0; setPosition(event.clientX - startX);
        verified = true; status.textContent = '正在验证…';
        // The server validates the protected challenge; the client never receives its answer.
        form.requestSubmit(submitter);
    });
    handle.addEventListener('pointercancel', reset);
    document.getElementById('slider-captcha-close').addEventListener('click', close);
    dialog.addEventListener('keydown', event => {
        if (event.key === 'Escape') { event.preventDefault(); close(); }
        if (event.key === 'Tab') {
            const buttons = Array.from(dialog.querySelectorAll('button:not(:disabled)'));
            const first = buttons[0], last = buttons[buttons.length - 1];
            if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
            else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
        }
    });
    window.addEventListener('resize', () => { if (!dialog.classList.contains('hidden')) reset(); });
    if (dialog.dataset.retry === 'true') {
        dialog.classList.remove('hidden');
        reset(); handle.focus();
        status.textContent = '未对准缺口，已更换图片，请重试';
    }
})();
