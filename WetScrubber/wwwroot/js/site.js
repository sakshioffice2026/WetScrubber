/* ═════════════════════════════════════════════════════════════
   site.js — shared UI helpers: toast, confirm dialog, loading overlay
   Usage:
     WS.toast('Saved', 'success');            // success | error | warning | info
     WS.confirm({ title, message }).then(ok => { ... });
     WS.loading.show('Calculating…');  WS.loading.hide();
   ═════════════════════════════════════════════════════════════ */
(function (window, document) {
    'use strict';

    var STYLE_ID = 'ws-ui-style';

    function injectStyles() {
        if (document.getElementById(STYLE_ID)) return;
        var css = [
            '.ws-toast-host{position:fixed;top:18px;right:18px;z-index:10000;display:flex;flex-direction:column;gap:10px;max-width:360px;pointer-events:none}',
            '.ws-toast{pointer-events:auto;display:flex;align-items:flex-start;gap:10px;padding:12px 14px;background:#fff;border:1px solid #e2e8f0;border-left-width:4px;border-radius:10px;box-shadow:0 8px 24px rgba(15,23,42,.12);font:600 13px/1.4 "Plus Jakarta Sans",-apple-system,BlinkMacSystemFont,sans-serif;color:#0f172a;opacity:0;transform:translateX(16px);transition:opacity .2s ease,transform .2s ease}',
            '.ws-toast.ws-in{opacity:1;transform:translateX(0)}',
            '.ws-toast-success{border-left-color:#10b981}',
            '.ws-toast-error{border-left-color:#ef4444}',
            '.ws-toast-warning{border-left-color:#f59e0b}',
            '.ws-toast-info{border-left-color:#3b82f6}',
            '.ws-toast-icon{flex:0 0 auto;font-size:15px;line-height:1.2}',
            '.ws-toast-success .ws-toast-icon{color:#10b981}',
            '.ws-toast-error .ws-toast-icon{color:#ef4444}',
            '.ws-toast-warning .ws-toast-icon{color:#f59e0b}',
            '.ws-toast-info .ws-toast-icon{color:#3b82f6}',
            '.ws-toast-msg{flex:1 1 auto;word-break:break-word}',
            '.ws-toast-close{flex:0 0 auto;background:none;border:0;color:#94a3b8;font-size:16px;line-height:1;cursor:pointer;padding:0 2px}',
            '.ws-toast-close:hover{color:#0f172a}',

            '.ws-modal-backdrop{position:fixed;inset:0;z-index:10001;background:rgba(15,23,42,.45);display:flex;align-items:center;justify-content:center;padding:16px;opacity:0;transition:opacity .15s ease}',
            '.ws-modal-backdrop.ws-in{opacity:1}',
            '.ws-modal{width:100%;max-width:400px;background:#fff;border-radius:14px;box-shadow:0 20px 50px rgba(15,23,42,.25);padding:22px;font-family:"Plus Jakarta Sans",-apple-system,BlinkMacSystemFont,sans-serif;transform:translateY(8px) scale(.98);transition:transform .15s ease}',
            '.ws-modal-backdrop.ws-in .ws-modal{transform:none}',
            '.ws-modal-title{margin:0 0 6px;font-size:16px;font-weight:700;color:#0f172a}',
            '.ws-modal-msg{margin:0 0 18px;font-size:13.5px;line-height:1.5;color:#475569}',
            '.ws-modal-actions{display:flex;justify-content:flex-end;gap:10px}',
            '.ws-btn{height:36px;padding:0 16px;border-radius:8px;font:600 13px "Plus Jakarta Sans",sans-serif;cursor:pointer;border:1px solid #e2e8f0;background:#fff;color:#334155}',
            '.ws-btn:hover{background:#f8fafc}',
            '.ws-btn:focus-visible{outline:2px solid #10b981;outline-offset:2px}',
            '.ws-btn-primary{background:#10b981;border-color:#10b981;color:#fff}',
            '.ws-btn-primary:hover{background:#059669}',
            '.ws-btn-danger{background:#ef4444;border-color:#ef4444;color:#fff}',
            '.ws-btn-danger:hover{background:#dc2626}',

            '.ws-loading{position:fixed;inset:0;z-index:10002;background:rgba(255,255,255,.7);backdrop-filter:blur(2px);display:none;align-items:center;justify-content:center;flex-direction:column;gap:14px}',
            '.ws-loading.ws-show{display:flex}',
            '.ws-spinner{width:42px;height:42px;border:4px solid #d1fae5;border-top-color:#10b981;border-radius:50%;animation:ws-spin .8s linear infinite}',
            '.ws-loading-text{font:600 13.5px "Plus Jakarta Sans",sans-serif;color:#334155}',
            '@keyframes ws-spin{to{transform:rotate(360deg)}}',
            '@media (prefers-reduced-motion:reduce){.ws-toast,.ws-modal,.ws-modal-backdrop{transition:none}.ws-spinner{animation-duration:2s}}'
        ].join('\n');
        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = css;
        document.head.appendChild(style);
    }

    var ICONS = { success: '✓', error: '✕', warning: '!', info: 'i' };
    var host = null;

    function getHost() {
        if (host && document.body.contains(host)) return host;
        host = document.createElement('div');
        host.className = 'ws-toast-host';
        host.setAttribute('role', 'region');
        host.setAttribute('aria-label', 'Notifications');
        document.body.appendChild(host);
        return host;
    }

    function toast(message, type, duration) {
        injectStyles();
        type = ICONS[type] ? type : 'info';
        duration = typeof duration === 'number' ? duration : (type === 'error' ? 7000 : 4000);

        var el = document.createElement('div');
        el.className = 'ws-toast ws-toast-' + type;
        el.setAttribute('role', type === 'error' ? 'alert' : 'status');

        var icon = document.createElement('span');
        icon.className = 'ws-toast-icon';
        icon.textContent = ICONS[type];

        var msg = document.createElement('span');
        msg.className = 'ws-toast-msg';
        msg.textContent = message;

        var close = document.createElement('button');
        close.type = 'button';
        close.className = 'ws-toast-close';
        close.setAttribute('aria-label', 'Dismiss');
        close.textContent = '×';

        el.appendChild(icon);
        el.appendChild(msg);
        el.appendChild(close);
        getHost().appendChild(el);

        requestAnimationFrame(function () { el.classList.add('ws-in'); });

        var timer = null;
        function dismiss() {
            if (timer) clearTimeout(timer);
            el.classList.remove('ws-in');
            setTimeout(function () { if (el.parentNode) el.parentNode.removeChild(el); }, 220);
        }
        close.addEventListener('click', dismiss);
        if (duration > 0) timer = setTimeout(dismiss, duration);
        return dismiss;
    }

    function confirmDialog(opts) {
        injectStyles();
        opts = opts || {};
        return new Promise(function (resolve) {
            var previouslyFocused = document.activeElement;

            var backdrop = document.createElement('div');
            backdrop.className = 'ws-modal-backdrop';

            var modal = document.createElement('div');
            modal.className = 'ws-modal';
            modal.setAttribute('role', 'alertdialog');
            modal.setAttribute('aria-modal', 'true');

            var title = document.createElement('h3');
            title.className = 'ws-modal-title';
            title.textContent = opts.title || 'Please confirm';
            title.id = 'ws-modal-title';
            modal.setAttribute('aria-labelledby', title.id);

            var msg = document.createElement('p');
            msg.className = 'ws-modal-msg';
            msg.textContent = opts.message || 'Are you sure?';

            var actions = document.createElement('div');
            actions.className = 'ws-modal-actions';

            var cancel = document.createElement('button');
            cancel.type = 'button';
            cancel.className = 'ws-btn';
            cancel.textContent = opts.cancelText || 'Cancel';

            var ok = document.createElement('button');
            ok.type = 'button';
            ok.className = 'ws-btn ' + (opts.danger ? 'ws-btn-danger' : 'ws-btn-primary');
            ok.textContent = opts.confirmText || 'Confirm';

            actions.appendChild(cancel);
            actions.appendChild(ok);
            modal.appendChild(title);
            modal.appendChild(msg);
            modal.appendChild(actions);
            backdrop.appendChild(modal);
            document.body.appendChild(backdrop);

            requestAnimationFrame(function () { backdrop.classList.add('ws-in'); });
            ok.focus();

            function close(result) {
                document.removeEventListener('keydown', onKey);
                backdrop.classList.remove('ws-in');
                setTimeout(function () {
                    if (backdrop.parentNode) backdrop.parentNode.removeChild(backdrop);
                    if (previouslyFocused && previouslyFocused.focus) previouslyFocused.focus();
                }, 160);
                resolve(result);
            }

            function onKey(e) {
                if (e.key === 'Escape') { e.preventDefault(); close(false); }
                else if (e.key === 'Tab') {
                    var focusables = [cancel, ok];
                    var idx = focusables.indexOf(document.activeElement);
                    e.preventDefault();
                    var next = e.shiftKey ? (idx <= 0 ? 1 : idx - 1) : (idx === 1 ? 0 : idx + 1);
                    focusables[next].focus();
                }
            }

            document.addEventListener('keydown', onKey);
            cancel.addEventListener('click', function () { close(false); });
            ok.addEventListener('click', function () { close(true); });
            backdrop.addEventListener('mousedown', function (e) { if (e.target === backdrop) close(false); });
        });
    }

    var loadingEl = null;
    var loadingText = null;
    var loadingCount = 0;

    function ensureLoading() {
        if (loadingEl && document.body.contains(loadingEl)) return;
        injectStyles();
        loadingEl = document.createElement('div');
        loadingEl.className = 'ws-loading';
        loadingEl.setAttribute('role', 'status');
        loadingEl.setAttribute('aria-live', 'polite');
        var spinner = document.createElement('div');
        spinner.className = 'ws-spinner';
        loadingText = document.createElement('div');
        loadingText.className = 'ws-loading-text';
        loadingEl.appendChild(spinner);
        loadingEl.appendChild(loadingText);
        document.body.appendChild(loadingEl);
    }

    var loading = {
        show: function (text) {
            ensureLoading();
            loadingCount++;
            loadingText.textContent = text || 'Loading…';
            loadingEl.classList.add('ws-show');
        },
        hide: function () {
            if (!loadingEl) return;
            loadingCount = Math.max(0, loadingCount - 1);
            if (loadingCount === 0) loadingEl.classList.remove('ws-show');
        }
    };

    // Declarative confirm: data-ws-confirm="Message" on a <form>, <button> or <a>
    //   optional: data-ws-confirm-title, data-ws-confirm-ok, data-ws-confirm-danger="true"
    function confirmOptions(el) {
        return {
            title: el.getAttribute('data-ws-confirm-title') || 'Please confirm',
            message: el.getAttribute('data-ws-confirm'),
            confirmText: el.getAttribute('data-ws-confirm-ok') || 'Confirm',
            danger: el.getAttribute('data-ws-confirm-danger') === 'true'
        };
    }

    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!form || !form.getAttribute || form.__wsConfirmed) return;
        if (form.getAttribute('data-ws-confirm') === null) return;
        e.preventDefault();
        var submitter = e.submitter || null;
        confirmDialog(confirmOptions(form)).then(function (ok) {
            if (!ok) return;
            form.__wsConfirmed = true;
            if (form.requestSubmit) { submitter ? form.requestSubmit(submitter) : form.requestSubmit(); }
            else { form.submit(); }
            setTimeout(function () { form.__wsConfirmed = false; }, 0);
        });
    });

    document.addEventListener('click', function (e) {
        var el = e.target && e.target.closest ? e.target.closest('[data-ws-confirm]') : null;
        if (!el || el.tagName === 'FORM' || el.__wsConfirmed) return;
        e.preventDefault();
        confirmDialog(confirmOptions(el)).then(function (ok) {
            if (!ok) return;
            if (el.tagName === 'A' && el.href) { window.location.href = el.href; return; }
            el.__wsConfirmed = true;
            el.click();
            setTimeout(function () { el.__wsConfirmed = false; }, 0);
        });
    });

    // Show the loading overlay on submit for forms marked data-ws-loading="Message"
    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!form || !form.getAttribute || e.defaultPrevented) return;
        var text = form.getAttribute('data-ws-loading');
        if (text === null) return;
        if (form.checkValidity && !form.checkValidity()) return;
        loading.show(text || 'Working…');
    });

    // Restore the page if the user returns via the back button
    window.addEventListener('pageshow', function (e) {
        if (e.persisted && loadingEl) { loadingCount = 0; loadingEl.classList.remove('ws-show'); }
    });

    window.WS = window.WS || {};
    window.WS.toast = toast;
    window.WS.confirm = confirmDialog;
    window.WS.loading = loading;
})(window, document);