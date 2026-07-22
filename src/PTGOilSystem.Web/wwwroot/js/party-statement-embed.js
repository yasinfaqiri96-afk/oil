/*
 * party-statement-embed.js — lazy-loads the official party statement into the
 * "صورت‌حساب" tab of a party profile, so the statement renders in place instead
 * of behind a navigation button. Fetches the standalone statement page, lifts
 * its scoped stylesheet + document markup into the current page, and rewires the
 * filter/print controls so they keep working inside the tab.
 *
 * Read-only: no statement/ledger figure is recomputed here — the server-rendered
 * document is shown verbatim.
 */
(function () {
    "use strict";

    var SPA_HEADERS = { "X-PTG-SPA": "1" };
    var STYLE_KEY = "party-statement";

    // The statement stylesheet ships as an @section Styles link on the standalone
    // page. When we inject only its body, that link must be carried into <head>
    // or the document renders unstyled (the same root cause the standalone page
    // used to hit under SPA navigation).
    function ensureStylesheet(sourceDoc) {
        var link = sourceDoc.querySelector('link[data-ptg-page-asset="' + STYLE_KEY + '"]');
        if (!link) {
            return;
        }
        if (document.querySelector('link[data-ptg-page-asset="' + STYLE_KEY + '"]')) {
            return;
        }
        var clone = document.createElement("link");
        clone.rel = "stylesheet";
        clone.href = link.getAttribute("href");
        clone.setAttribute("data-ptg-page-asset", STYLE_KEY);
        document.head.appendChild(clone);
    }

    // نشانی سند مستقل با فیلترهای فعلی نوار فیلتر + print=true.
    function buildPrintUrl(url, form) {
        var target = new URL(url, window.location.origin);
        if (form) {
            new FormData(form).forEach(function (value, key) {
                if (value !== "") {
                    target.searchParams.set(key, value);
                }
            });
        }
        target.searchParams.set("print", "true");
        return target.toString();
    }

    function bindTools(host, url) {
        // The filter bar belongs to the full statement page; applying a filter
        // opens the official document rather than trying to re-embed.
        var form = host.querySelector(".statement-filter-bar");
        if (form) {
            form.setAttribute("action", url);
            form.setAttribute("method", "get");
        }
        // چاپ داخل تب پروفایل کل صفحه را چاپ می‌کرد و خروجی خالی/ناقص می‌داد.
        // به‌جای آن سند مستقل با print=true باز می‌شود و خودش چاپ را اجرا می‌کند.
        host.querySelectorAll("[data-statement-print]").forEach(function (btn) {
            btn.addEventListener("click", function () {
                window.open(buildPrintUrl(url, form), "_blank", "noopener");
            });
        });
    }

    function render(host, html, url) {
        var doc = new DOMParser().parseFromString(html, "text/html");
        ensureStylesheet(doc);

        var article = doc.querySelector(".statement-document");
        if (!article) {
            host.innerHTML = '<div class="ak-empty ak-detail-empty-state">'
                + '<i class="bi bi-file-earmark-text" aria-hidden="true"></i>'
                + '<p>صورت‌حساب یافت نشد. <a href="' + url + '">باز کردن صفحه کامل</a></p></div>';
            return;
        }

        var tools = doc.querySelector(".statement-screen-tools");
        host.innerHTML = "";
        if (tools) {
            host.appendChild(document.importNode(tools, true));
        }
        host.appendChild(document.importNode(article, true));
        bindTools(host, url);
    }

    function load(host) {
        // Only load when the statement tab is actually visible. On other tabs the
        // section is present but [hidden]; the page reloads per server tab, so a
        // later visit re-runs this with the section shown.
        if (host.dataset.embedState || host.closest("[hidden]")) {
            return;
        }
        var url = host.getAttribute("data-party-statement-embed");
        if (!url) {
            return;
        }
        host.dataset.embedState = "loading";
        host.classList.add("is-loading");
        host.setAttribute("aria-busy", "true");

        fetch(url, { credentials: "same-origin", headers: SPA_HEADERS })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error("status " + response.status);
                }
                return response.text();
            })
            .then(function (html) {
                render(host, html, url);
                host.dataset.embedState = "loaded";
            })
            .catch(function () {
                host.innerHTML = '<div class="ak-empty ak-detail-empty-state">'
                    + '<i class="bi bi-exclamation-triangle" aria-hidden="true"></i>'
                    + '<p>خطا در بارگذاری صورت‌حساب. <a href="' + url + '">باز کردن صفحه کامل</a></p></div>';
                host.dataset.embedState = "error";
            })
            .then(function () {
                host.classList.remove("is-loading");
                host.removeAttribute("aria-busy");
            });
    }

    function init() {
        Array.prototype.slice
            .call(document.querySelectorAll("[data-party-statement-embed]"))
            .forEach(load);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init, { once: true });
    } else {
        init();
    }

    // Re-scan after a SPA swap so the tab lazy-loads on its first reveal.
    window.addEventListener("ptg:page-ready", init);
})();
