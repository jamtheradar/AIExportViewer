// Local helpers only. No network calls, no third-party code.
window.aiev = (function () {
    let links = null;
    let shell = null;

    function linkFrom(target) {
        const el = target && target.closest ? target.closest('[data-href]') : null;
        return el ? el.getAttribute('data-href') : null;
    }

    function onClick(e) {
        const url = linkFrom(e.target);
        if (url && links) {
            e.preventDefault();
            links.invokeMethodAsync('Request', url);
        }
    }

    function onKey(e) {
        const mod = e.ctrlKey || e.metaKey;
        if (e.key === 'Enter') {
            const url = linkFrom(e.target);
            if (url && links) { e.preventDefault(); links.invokeMethodAsync('Request', url); return; }
        }
        if (!mod || e.altKey) return;
        const k = e.key.toLowerCase();
        if (k === 'f') {
            e.preventDefault();
            const box = document.getElementById('global-search');
            if (box) { box.focus(); box.select(); }
        } else if (k === 'o' && shell) {
            e.preventDefault();
            shell.invokeMethodAsync('OpenLibraryShortcut');
        }
    }

    return {
        /** Goes back like the browser's Back button. False when there is nowhere to go back to. */
        back: function () {
            if (window.history.length <= 1) return false;
            window.history.back();
            return true;
        },
        init: function (linkRef, shellRef) {
            links = linkRef;
            shell = shellRef;
            document.removeEventListener('click', onClick, true);
            document.removeEventListener('keydown', onKey, true);
            document.addEventListener('click', onClick, true);
            document.addEventListener('keydown', onKey, true);
        },
        scrollToId: function (id) {
            const el = document.getElementById(id);
            if (el) {
                el.scrollIntoView({ block: 'center' });
                el.classList.add('flash');
                setTimeout(function () { el.classList.remove('flash'); }, 2000);
            }
        },
        setTheme: function (theme) {
            document.documentElement.setAttribute('data-theme', theme === 'dark' ? 'dark' : 'light');
        },
        scrollTop: function () {
            const main = document.querySelector('.main-scroll');
            if (main) main.scrollTop = 0;
        }
    };
})();
