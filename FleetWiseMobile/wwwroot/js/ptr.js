// Pull-to-refresh for the tab pages. Wired once to the persistent .app-body
// scroll container; each tab page registers its own .NET refresh callback via
// window.ptr.set(dotnetRef). Pulling down at scrollTop 0 past a threshold calls
// the current page's [JSInvokable] Refresh().
window.ptr = (function () {
    let current = null;
    let wired = false;
    const THRESHOLD = 70;

    function indicator() {
        let el = document.getElementById('ptr-ind');
        return el;
    }

    function wire() {
        const el = document.querySelector('.app-body');
        if (!el) return false;

        let startY = 0, dist = 0, pulling = false, busy = false;
        const ind = indicator();

        el.addEventListener('touchstart', (e) => {
            if (busy) return;
            if (el.scrollTop <= 0) { startY = e.touches[0].clientY; pulling = true; dist = 0; }
        }, { passive: true });

        el.addEventListener('touchmove', (e) => {
            if (!pulling || busy) return;
            dist = e.touches[0].clientY - startY;
            if (dist > 0 && el.scrollTop <= 0 && ind) {
                ind.style.height = Math.min(dist * 0.5, THRESHOLD) + 'px';
                ind.style.opacity = Math.min(dist / THRESHOLD, 1);
            }
        }, { passive: true });

        el.addEventListener('touchend', async () => {
            if (!pulling) return;
            pulling = false;
            const trigger = dist > THRESHOLD;
            if (trigger && current && !busy) {
                busy = true;
                if (ind) { ind.style.height = THRESHOLD + 'px'; ind.classList.add('spin'); }
                try { await current.invokeMethodAsync('Refresh'); } catch (e) { }
                busy = false;
            }
            if (ind) { ind.style.height = '0px'; ind.style.opacity = 0; ind.classList.remove('spin'); }
        });

        return true;
    }

    return {
        set: function (dotnet) {
            current = dotnet;
            if (!wired) { wired = wire(); }
        }
    };
})();
