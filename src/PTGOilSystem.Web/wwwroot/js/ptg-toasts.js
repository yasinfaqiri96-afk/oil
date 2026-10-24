/*
 * PTG global toast notifications + destructive-action confirm dialog.
 * - Activates server-rendered toasts from _ToastNotifications.cshtml.
 * - Exposes window.ptgToast(type, message, title, opts) for programmatic use.
 * - Intercepts [data-ptg-confirm="true"] forms/links with a compact RTL dialog.
 * UI only. No business logic.
 */
(function () {
    "use strict";

    var DEFAULT_DURATION = 5000;
    var ICONS = {
        success: "bi-check-circle-fill",
        error: "bi-exclamation-octagon-fill",
        warning: "bi-exclamation-triangle-fill",
        info: "bi-info-circle-fill"
    };
    var DEFAULT_TITLES = {
        success: "انجام شد",
        error: "خطا",
        warning: "هشدار",
        info: "اطلاع"
    };

    function reduceMotion() {
        return window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    }

    function getHost() {
        var host = document.querySelector("[data-ptg-toast-host]");
        if (!host) {
            host = document.createElement("div");
            host.className = "ptg-toast-host";
            host.setAttribute("data-ptg-toast-host", "");
            host.setAttribute("aria-live", "polite");
            host.setAttribute("aria-relevant", "additions");
            document.body.appendChild(host);
        }
        return host;
    }

    function dismiss(toast) {
        if (!toast || toast.dataset.ptgLeaving === "1") {
            return;
        }
        toast.dataset.ptgLeaving = "1";
        if (toast._ptgTimer) {
            clearTimeout(toast._ptgTimer);
        }
        if (reduceMotion()) {
            toast.remove();
            return;
        }
        toast.classList.add("is-leaving");
        toast.classList.remove("is-visible");
        var removed = false;
        var done = function () {
            if (removed) { return; }
            removed = true;
            toast.remove();
        };
        toast.addEventListener("transitionend", done, { once: true });
        setTimeout(done, 400);
    }

    function activate(toast) {
        if (!toast || toast.dataset.ptgActivated === "1") {
            return;
        }
        toast.dataset.ptgActivated = "1";

        var closeBtn = toast.querySelector("[data-ptg-toast-close]");
        if (closeBtn) {
            closeBtn.addEventListener("click", function () { dismiss(toast); });
        }

        var duration = parseInt(toast.getAttribute("data-ptg-toast-duration"), 10);
        if (isNaN(duration)) { duration = DEFAULT_DURATION; }

        // entrance
        requestAnimationFrame(function () {
            toast.classList.add("is-visible");
            if (duration > 0) {
                toast.style.setProperty("--ptg-toast-duration", duration + "ms");
                toast.classList.add("is-counting");
            }
        });

        if (duration > 0) {
            var start = function () {
                toast._ptgTimer = setTimeout(function () { dismiss(toast); }, duration);
            };
            start();
            // pause on hover
            toast.addEventListener("mouseenter", function () {
                if (toast._ptgTimer) { clearTimeout(toast._ptgTimer); }
                toast.classList.remove("is-counting");
            });
            toast.addEventListener("mouseleave", function () {
                if (toast.dataset.ptgLeaving === "1") { return; }
                toast.style.setProperty("--ptg-toast-duration", duration + "ms");
                // restart bar
                void toast.offsetWidth;
                toast.classList.add("is-counting");
                start();
            });
        }
    }

    function scanToasts(root) {
        var scope = root || document;
        var nodes = scope.querySelectorAll("[data-ptg-toast]");
        for (var i = 0; i < nodes.length; i++) {
            activate(nodes[i]);
        }
    }

    // ---- Programmatic API ----
    function showToast(type, message, title, opts) {
        type = ICONS[type] ? type : "info";
        opts = opts || {};
        var host = getHost();
        var toast = document.createElement("div");
        toast.className = "ptg-toast ptg-toast--" + type;
        toast.setAttribute("data-ptg-toast", "");
        toast.setAttribute("role", type === "error" ? "alert" : "status");
        var duration = typeof opts.duration === "number" ? opts.duration : DEFAULT_DURATION;
        toast.setAttribute("data-ptg-toast-duration", String(duration));

        var icon = document.createElement("span");
        icon.className = "ptg-toast__icon";
        icon.setAttribute("aria-hidden", "true");
        icon.innerHTML = '<i class="bi ' + ICONS[type] + '"></i>';

        var body = document.createElement("div");
        body.className = "ptg-toast__body";
        var titleEl = document.createElement("strong");
        titleEl.className = "ptg-toast__title";
        titleEl.textContent = title || DEFAULT_TITLES[type];
        var msgEl = document.createElement("div");
        msgEl.className = "ptg-toast__message";
        msgEl.textContent = message || "";
        body.appendChild(titleEl);
        body.appendChild(msgEl);

        var close = document.createElement("button");
        close.type = "button";
        close.className = "ptg-toast__close";
        close.setAttribute("data-ptg-toast-close", "");
        close.setAttribute("aria-label", "بستن");
        close.innerHTML = '<i class="bi bi-x-lg" aria-hidden="true"></i>';

        var bar = document.createElement("span");
        bar.className = "ptg-toast__bar";
        bar.setAttribute("aria-hidden", "true");

        toast.appendChild(icon);
        toast.appendChild(body);
        toast.appendChild(close);
        toast.appendChild(bar);
        host.appendChild(toast);
        activate(toast);
        return toast;
    }

    // ---- Confirm dialog ----
    // danger: حذف/غیرفعال‌سازی (پیش‌فرض)؛ primary: تأیید عملیات عادی با data-ptg-confirm-tone="primary".
    var CONFIRM_ICONS = {
        danger: "bi-exclamation-triangle-fill",
        primary: "bi-info-circle-fill"
    };

    function getDialog() {
        return document.querySelector("[data-ptg-confirm-dialog]");
    }

    var activeConfirm = null;

    function closeConfirm(dialog) {
        if (!dialog) { return; }
        dialog.classList.remove("is-open");
        var finish = function () {
            dialog.hidden = true;
            dialog.setAttribute("aria-hidden", "true");
        };
        if (reduceMotion()) { finish(); }
        else { setTimeout(finish, 180); }
        activeConfirm = null;
    }

    function openConfirm(opts) {
        var dialog = getDialog();
        if (!dialog) {
            // no dialog on page -> fall back to native confirm
            return Promise.resolve(window.confirm(opts.message || "آیا مطمئن هستید؟"));
        }
        var titleEl = dialog.querySelector("[data-ptg-confirm-title]");
        var textEl = dialog.querySelector("[data-ptg-confirm-text]");
        var okBtn = dialog.querySelector("[data-ptg-confirm-ok]");
        var iconEl = dialog.querySelector(".ptg-confirm__icon i");
        var tone = CONFIRM_ICONS[opts.tone] ? opts.tone : "danger";
        dialog.setAttribute("data-tone", tone);
        if (iconEl) { iconEl.className = "bi " + CONFIRM_ICONS[tone]; }
        if (titleEl && opts.title) { titleEl.textContent = opts.title; }
        if (textEl && opts.message) { textEl.textContent = opts.message; }
        if (okBtn) {
            // برچسب پیش‌فرض یک‌بار نگه داشته می‌شود تا برچسب سفارشی تأیید قبلی به تأیید بعدی نچسبد.
            if (!okBtn.hasAttribute("data-default-label")) { okBtn.setAttribute("data-default-label", okBtn.textContent); }
            okBtn.textContent = opts.confirmLabel || okBtn.getAttribute("data-default-label");
        }

        dialog.hidden = false;
        dialog.setAttribute("aria-hidden", "false");
        requestAnimationFrame(function () { dialog.classList.add("is-open"); });
        if (okBtn) { okBtn.focus(); }

        return new Promise(function (resolve) {
            activeConfirm = { dialog: dialog, resolve: resolve };
        });
    }

    function resolveConfirm(result) {
        if (!activeConfirm) { return; }
        var ctx = activeConfirm;
        closeConfirm(ctx.dialog);
        ctx.resolve(result);
    }

    function bindConfirmDialog() {
        // delegated clicks within any confirm dialog (survives SPA swaps)
        document.addEventListener("click", function (event) {
            if (!activeConfirm) { return; }
            if (event.target.closest("[data-ptg-confirm-ok]")) {
                resolveConfirm(true);
            } else if (event.target.closest("[data-ptg-confirm-cancel]")) {
                resolveConfirm(false);
            }
        });
        document.addEventListener("keydown", function (event) {
            if (activeConfirm && event.key === "Escape") {
                resolveConfirm(false);
            }
        });
    }

    function confirmOptionsFrom(el) {
        return {
            title: el.getAttribute("data-ptg-confirm-title") || "تأیید عملیات",
            message: el.getAttribute("data-ptg-confirm-message") || "آیا از انجام این عملیات مطمئن هستید؟",
            confirmLabel: el.getAttribute("data-ptg-confirm-ok-label") || null,
            tone: el.getAttribute("data-ptg-confirm-tone") || null
        };
    }

    function bindConfirmTriggers() {
        // forms
        document.addEventListener("submit", function (event) {
            var form = event.target;
            if (!form || form.getAttribute("data-ptg-confirm") !== "true") { return; }
            if (form.dataset.ptgConfirmed === "1") {
                form.dataset.ptgConfirmed = "";
                return; // allow real submit
            }
            event.preventDefault();
            openConfirm(confirmOptionsFrom(form)).then(function (ok) {
                if (ok) {
                    form.dataset.ptgConfirmed = "1";
                    if (typeof form.requestSubmit === "function") {
                        form.requestSubmit();
                    } else {
                        form.submit();
                    }
                }
            });
        }, true);

        // links / standalone buttons
        document.addEventListener("click", function (event) {
            var trigger = event.target.closest('a[data-ptg-confirm="true"], button[data-ptg-confirm="true"]');
            if (!trigger) { return; }
            if (trigger.closest("form") && trigger.type === "submit") { return; } // handled by submit
            if (trigger.dataset.ptgConfirmed === "1") {
                trigger.dataset.ptgConfirmed = "";
                return;
            }
            event.preventDefault();
            openConfirm(confirmOptionsFrom(trigger)).then(function (ok) {
                if (!ok) { return; }
                if (trigger.tagName === "A" && trigger.href) {
                    window.location.href = trigger.href;
                } else {
                    trigger.dataset.ptgConfirmed = "1";
                    trigger.click();
                }
            });
        });
    }

    // ---- Boot ----
    function init() {
        scanToasts();
    }

    // expose API
    window.ptgToast = showToast;
    window.ptgConfirm = openConfirm;

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", function () {
            bindConfirmDialog();
            bindConfirmTriggers();
            init();
        }, { once: true });
    } else {
        bindConfirmDialog();
        bindConfirmTriggers();
        init();
    }

    // re-scan after SPA partial navigation (spa-nav.js fires this)
    window.addEventListener("ptg:page-ready", function () { scanToasts(); });
})();
