// party-settlements.js
// فورم «تسویه بین طرف‌حساب‌ها»: بعد از انتخاب هر طرف‌حساب، ماندهٔ فعلی‌اش با شرکت را
// به زبان ساده زیر همان لیست نشان می‌دهد. فقط نمایش است؛ هیچ مقداری در فورم تغییر نمی‌کند.
(function () {
    "use strict";

    function show(select) {
        const form = select.closest("form[data-party-balance-url]");
        const note = form && form.querySelector('[data-party-balance-for="' + select.id + '"]');
        if (!note) {
            return;
        }

        note.textContent = "";
        if (!select.value) {
            return;
        }

        const url = form.getAttribute("data-party-balance-url") + "?party=" + encodeURIComponent(select.value);
        fetch(url, { headers: { "Accept": "application/json" }, credentials: "same-origin" })
            .then(function (response) { return response.ok ? response.json() : null; })
            .then(function (data) {
                if (data && select.value) {
                    note.textContent = "مانده فعلی با شرکت — " + data.text;
                }
            })
            .catch(function () { /* نمایش مانده اختیاری است؛ خطا فورم را متوقف نمی‌کند. */ });
    }

    function init() {
        document.querySelectorAll("[data-party-balance-select]").forEach(show);
    }

    // ناوبری SPA ممکن است اسکریپت را دوباره اجرا کند؛ listenerها فقط یک بار ثبت می‌شوند.
    if (window.__ptgPartySettlementsReady) {
        init();
        return;
    }
    window.__ptgPartySettlementsReady = true;

    document.addEventListener("change", function (event) {
        const target = event.target;
        if (target && target.matches && target.matches("[data-party-balance-select]")) {
            show(target);
        }
    });

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
    window.addEventListener("ptg:page-ready", init);
})();
