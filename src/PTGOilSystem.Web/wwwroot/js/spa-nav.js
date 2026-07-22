(function () {
    "use strict";

    var navigating = false;
    var pageStyleLoadTimeoutMs = 1800;

    // --- Prefetch cache (perceived-instant navigation) ----------------------
    // Hover/mousedown over an internal link warms the page HTML so the eventual
    // click is served from memory instead of waiting on a fresh server render.
    var prefetchCache = {};        // url -> { html, finalUrl, ts }
    var prefetchInFlight = {};     // url -> true while fetching
    var prefetchTtlMs = 15000;     // keep short: finance data must stay fresh
    var prefetchMax = 24;          // cap memory
    var hoverDelayMs = 65;         // ignore quick mouse sweeps
    var hoverTimer = null;
    var spaHeaders = { "X-PTG-SPA": "1" };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init, { once: true });
    } else {
        init();
    }

    function init() {
        history.replaceState({ ptgSpa: true }, document.title, location.href);
        document.addEventListener("click", onClick, true);
        document.addEventListener("submit", onSubmit, true);
        document.addEventListener("mouseover", onHover, true);
        document.addEventListener("mousedown", onPressPrefetch, true);
        document.addEventListener("touchstart", onPressPrefetch, { capture: true, passive: true });
        document.addEventListener("focusin", onHover, true);
        window.addEventListener("popstate", onPopState);
        // bfcache / Back-Forward: اگر صفحه وسط swap ذخیره شده باشد، مخفی نماند.
        window.addEventListener("pageshow", function () {
            var main = document.querySelector("main");
            if (main) main.classList.remove("ptg-page-swap");
        });
    }

    function prefetchableLink(target) {
        var a = target && target.closest ? target.closest("a[href]") : null;
        if (!a || !shouldIntercept(a)) return null;
        // Only GET-style nav links; skip anything that mutates or is non-idempotent.
        if (a.hasAttribute("data-no-prefetch")) return null;
        var url = assetUrl(a);
        if (!url || url === location.href) return null;
        return url;
    }

    function onHover(e) {
        var url = prefetchableLink(e.target);
        if (!url) return;
        clearTimeout(hoverTimer);
        hoverTimer = setTimeout(function () { prefetch(url); }, hoverDelayMs);
    }

    function onPressPrefetch(e) {
        var url = prefetchableLink(e.target);
        if (url) prefetch(url);
    }

    function freshCacheEntry(url) {
        var hit = prefetchCache[url];
        if (hit && (Date.now() - hit.ts) <= prefetchTtlMs) return hit;
        if (hit) delete prefetchCache[url];
        return null;
    }

    function prefetch(url) {
        if (navigating || freshCacheEntry(url) || prefetchInFlight[url]) return;
        var keys = Object.keys(prefetchCache);
        if (keys.length >= prefetchMax) delete prefetchCache[keys[0]];
        prefetchInFlight[url] = true;
        fetch(url, { method: "GET", credentials: "same-origin", redirect: "follow", headers: spaHeaders })
            .then(function (res) {
                if (!res.ok) return null;
                return res.text().then(function (html) {
                    prefetchCache[url] = { html: html, finalUrl: res.url, ts: Date.now() };
                });
            })
            .catch(function () {})
            .finally(function () { delete prefetchInFlight[url]; });
    }

    function onClick(e) {
        if (e.defaultPrevented || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
        var a = e.target.closest("a[href]");
        if (!a || !shouldIntercept(a)) return;
        e.preventDefault();
        e.stopPropagation();
        go(a.href, "GET", null, true);
    }

    function shouldIntercept(a) {
        var href = a.getAttribute("href") || "";
        if (!href || href === "#" || href.startsWith("#") || href.startsWith("javascript")) return false;
        if (a.target && a.target !== "_self") return false;
        if (a.hasAttribute("download")) return false;
        if (a.hasAttribute("data-no-spa")) return false;
        if (a.hasAttribute("data-bs-toggle") || a.hasAttribute("data-bs-dismiss")) return false;
        try {
            var url = new URL(href, location.origin);
            if (url.origin !== location.origin) return false;
        } catch (_) { return false; }
        return true;
    }

    function onSubmit(e) {
        if (e.defaultPrevented) return;
        var form = e.target;
        if (!form || form.tagName !== "FORM") return;
        if (form.hasAttribute("data-no-spa")) return;
        if (form.enctype === "multipart/form-data") return;
        var action = form.getAttribute("action") || location.href;
        if (/logout/i.test(action)) return;
        e.preventDefault();
        e.stopPropagation();
        var method = (form.method || "get").toUpperCase();
        // new FormData(form) به‌تنهایی مقدار دکمهٔ submit کلیک‌شده را شامل نمی‌شود؛
        // بدون این، فیلدهایی مثل SubmissionMode هرگز به سرور نمی‌رسند.
        var data = buildFormData(form, e.submitter);
        if (method === "POST") {
            go(action, "POST", data, true);
        } else {
            var params = new URLSearchParams(data);
            var url = action.split("?")[0] + (params.toString() ? "?" + params : "");
            go(url, "GET", null, true);
        }
    }

    function buildFormData(form, submitter) {
        try {
            // مرورگرهای جدید: submitter را خودشان اضافه می‌کنند.
            if (submitter) return new FormData(form, submitter);
        } catch (_) { /* مرورگر قدیمی: پایین دستی اضافه می‌شود */ }
        var fd = new FormData(form);
        if (submitter && submitter.name && !submitter.disabled && !fd.has(submitter.name)) {
            fd.append(submitter.name, submitter.value);
        }
        return fd;
    }

    function onPopState(e) {
        if (e.state && e.state.ptgSpa) {
            go(location.href, "GET", null, false);
        }
    }

    // Public hooks: let other scripts (e.g. modal AJAX submit) drive SPA
    // navigation instead of a full page reload.
    window.PTG = window.PTG || {};
    window.PTG.spaNavigate = function (url) {
        try { go(url, "GET", null, true); } catch (_) { fallback(url); }
    };
    // Apply already-fetched HTML (e.g. the redirect target of a modal save) so
    // we don't issue a second GET that would consume read-once TempData flash.
    window.PTG.spaApplyHtml = function (url, html) {
        try {
            return Promise.resolve(swap(html, url, true)).catch(function () { fallback(url); });
        } catch (_) {
            fallback(url);
        }
    };

    function go(url, method, body, push) {
        if (navigating) return;
        navigating = true;
        loaderStart();

        // Serve from prefetch cache when a fresh warm copy exists (GET only).
        if (method === "GET" && !body) {
            var cached = freshCacheEntry(url);
            if (cached) {
                delete prefetchCache[url];
                Promise.resolve(swap(cached.html, cached.finalUrl, push))
                    .catch(function () { fallback(url); })
                    .finally(function () { navigating = false; loaderDone(); });
                return;
            }
        }

        var opts = { method: method, credentials: "same-origin", redirect: "follow", headers: spaHeaders };
        if (body) opts.body = body;
        fetch(url, opts)
            .then(function (res) {
                if (!res.ok && res.status >= 500) { fallback(url); return null; }
                return res.text().then(function (html) { return { html: html, finalUrl: res.url }; });
            })
            .then(function (result) {
                if (result) {
                    return swap(result.html, result.finalUrl, push);
                }
            })
            .catch(function () { fallback(url); })
            .finally(function () { navigating = false; loaderDone(); });
    }

    function cleanupBootstrapOverlays() {
        // Dispose / hide any open Bootstrap modals
        document.querySelectorAll(".modal.show").forEach(function (modalEl) {
            if (window.bootstrap && window.bootstrap.Modal) {
                try {
                    var inst = window.bootstrap.Modal.getInstance(modalEl);
                    if (inst) { inst.dispose(); }
                } catch (_) {}
            }
            modalEl.classList.remove("show");
            modalEl.style.display = "";
            modalEl.setAttribute("aria-hidden", "true");
            modalEl.removeAttribute("aria-modal");
            modalEl.removeAttribute("role");
        });
        // Remove any leftover backdrops (modal & offcanvas)
        document.querySelectorAll(".modal-backdrop, .offcanvas-backdrop").forEach(function (el) { el.remove(); });
        // Remove body classes & inline styles Bootstrap adds
        document.body.classList.remove("modal-open", "offcanvas-open");
        document.body.style.removeProperty("overflow");
        document.body.style.removeProperty("padding-right");
        document.body.style.removeProperty("padding-left");
        // Close mobile sidebar if open
        document.body.classList.remove("is-shell-nav-open");
    }

    function swap(html, url, push) {
        var parser = new DOMParser();
        var doc = parser.parseFromString(html, "text/html");

        var newMain = doc.querySelector("main");
        var curMain = document.querySelector("main");
        if (!newMain || !curMain) { fallback(url); return; }

        // If auth shell structure changed (login↔app), do full navigation
        var curHasShell = !!document.querySelector(".boltz-shell-frame");
        var newHasShell = !!doc.querySelector(".boltz-shell-frame");
        if (curHasShell !== newHasShell) { fallback(url); return; }

        // Only swap if both pages have same shell type
        if (curMain.classList.contains("boltz-public-shell") !== newMain.classList.contains("boltz-public-shell")) {
            fallback(url); return;
        }

        return preloadPageStyles(doc).then(function () {
            // محتوای تازه تا پایان همهٔ کارهای DOM (اسکریپت‌ها، بازچینی list-shell،
            // تب‌ها) مخفی می‌ماند و بعد یک‌جا با fade واحد ظاهر می‌شود؛ وگرنه
            // بازچینی‌ها وسط انیمیشن دیده می‌شوند و صفحه بندبند به‌نظر می‌رسد.
            curMain.classList.add("ptg-page-swap");
            try {
                // Clean up Bootstrap overlays & mobile sidebar before DOM swap
                cleanupBootstrapOverlays();
                syncDocumentShell(doc);

                curMain.className = newMain.className + " ptg-page-swap";
                curMain.innerHTML = newMain.innerHTML;
                syncPageAssets(doc);

                var newPageScripts = doc.getElementById("ptg-page-scripts");
                var curPageScripts = document.getElementById("ptg-page-scripts");
                if (newPageScripts && curPageScripts) {
                    curPageScripts.innerHTML = "";
                    execScripts(newPageScripts, curPageScripts);
                }

                document.title = doc.title;
                updateActiveNav(url);

                if (typeof window.__ptgReinit === "function") {
                    window.__ptgReinit();
                }

                execScripts(curMain, curMain);

                if (typeof window.__ptgApplyLanguage === "function") {
                    window.__ptgApplyLanguage();
                }

                window.dispatchEvent(new CustomEvent("ptg:page-ready", {
                    detail: { url: url }
                }));

                if (push) {
                    history.pushState({ ptgSpa: true }, document.title, url);
                } else {
                    history.replaceState({ ptgSpa: true }, document.title, url);
                }

                window.scrollTo(0, 0);
            } finally {
                revealMain(curMain);
            }
        });
    }

    function revealMain(main) {
        // یک فریم صبر تا مرورگر حالت مخفی را ثبت کند، بعد fade واحد؛
        // remove+reflow+add برای replay شدن انیمیشن در ناوبری‌های بعدی.
        requestAnimationFrame(function () {
            main.classList.remove("ptg-page-swap", "ptg-page-reveal");
            void main.offsetWidth;
            main.classList.add("ptg-page-reveal");
        });
    }

    function syncDocumentShell(doc) {
        var nextBody = doc.body;
        var nextHtml = doc.documentElement;

        if (nextHtml) {
            copyAttribute(nextHtml, document.documentElement, "lang");
            copyAttribute(nextHtml, document.documentElement, "dir");
        }

        if (!nextBody) return;

        document.body.className = nextBody.className;
        Array.from(document.body.attributes).forEach(function (attribute) {
            if (attribute.name.indexOf("data-") === 0 && !nextBody.hasAttribute(attribute.name)) {
                document.body.removeAttribute(attribute.name);
            }
        });
        Array.from(nextBody.attributes).forEach(function (attribute) {
            if (attribute.name === "class") return;
            if (attribute.name.indexOf("data-") === 0) {
                document.body.setAttribute(attribute.name, attribute.value);
            }
        });
    }

    function copyAttribute(source, target, name) {
        if (source.hasAttribute(name)) {
            target.setAttribute(name, source.getAttribute(name) || "");
        } else {
            target.removeAttribute(name);
        }
    }

    function preloadPageStyles(doc) {
        var selector = "link[rel~=\"stylesheet\"][data-ptg-page-asset]";
        var styles = Array.from(doc.querySelectorAll(selector));
        if (!styles.length) {
            return Promise.resolve();
        }

        return Promise.all(styles.map(function (style) {
            var key = style.getAttribute("data-ptg-page-asset") || "";
            if (!key) {
                return Promise.resolve();
            }

            var existing = document.querySelector("[data-ptg-page-asset=\"" + cssEscape(key) + "\"]");
            if (existing && assetUrl(existing) === assetUrl(style)) {
                return Promise.resolve();
            }

            var clone = cloneAsset(style);
            if (existing) {
                existing.remove();
            }

            return new Promise(function (resolve) {
                var done = false;
                var timer = window.setTimeout(finish, pageStyleLoadTimeoutMs);

                function finish() {
                    if (done) return;
                    done = true;
                    window.clearTimeout(timer);
                    resolve();
                }

                clone.addEventListener("load", finish, { once: true });
                clone.addEventListener("error", finish, { once: true });
                document.head.appendChild(clone);
            });
        }));
    }

    function syncPageAssets(doc) {
        var selector = "[data-ptg-page-asset]";
        var nextAssets = Array.from(doc.querySelectorAll(selector));
        var nextKeys = nextAssets.map(function (asset) {
            return asset.getAttribute("data-ptg-page-asset") || "";
        }).filter(Boolean);

        document.querySelectorAll(selector).forEach(function (asset) {
            var key = asset.getAttribute("data-ptg-page-asset") || "";
            if (!nextKeys.includes(key)) {
                asset.remove();
            }
        });

        nextAssets.forEach(function (asset) {
            var key = asset.getAttribute("data-ptg-page-asset") || "";
            if (!key) {
                return;
            }

            var existing = document.querySelector(selector + "[data-ptg-page-asset=\"" + cssEscape(key) + "\"]");
            if (existing && assetUrl(existing) === assetUrl(asset)) {
                return;
            }

            var clone = cloneAsset(asset);
            if (existing) {
                existing.remove();
            }

            if (clone.tagName === "LINK") {
                document.head.appendChild(clone);
            } else if (clone.tagName === "SCRIPT") {
                document.body.insertBefore(clone, document.getElementById("ptg-page-scripts"));
            }
        });
    }

    function cloneAsset(asset) {
        var clone = document.createElement(asset.tagName.toLowerCase());
        Array.from(asset.attributes).forEach(function (attribute) {
            clone.setAttribute(attribute.name, attribute.value);
        });
        clone.textContent = asset.textContent;
        if (clone.tagName === "SCRIPT") {
            clone.async = false;
        }

        return clone;
    }

    function assetUrl(asset) {
        var url = asset.getAttribute("href") || asset.getAttribute("src") || "";
        if (!url) {
            return "";
        }

        try {
            return new URL(url, location.href).href;
        } catch (_) {
            return url;
        }
    }

    function cssEscape(value) {
        if (window.CSS && typeof window.CSS.escape === "function") {
            return window.CSS.escape(value);
        }

        return String(value).replace(/["\\]/g, "\\$&");
    }

    function execScripts(source, container) {
        source.querySelectorAll("script").forEach(function (old) {
            var s = document.createElement("script");
            Array.from(old.attributes).forEach(function (a) { s.setAttribute(a.name, a.value); });
            s.textContent = old.textContent;
            s.async = false;
            if (container.contains(old)) {
                old.parentNode.replaceChild(s, old);
            } else {
                container.appendChild(s);
            }
        });
    }

    function updateActiveNav(url) {
        try {
            var pathname = new URL(url, location.origin).pathname;
            document.querySelectorAll(".boltz-nav-link").forEach(function (link) {
                var href = link.getAttribute("href") || "";
                if (!href || href.startsWith("#")) return;
                var lp;
                try { lp = new URL(href, location.origin).pathname; } catch (_) { return; }
                var active = lp === "/"
                    ? pathname === "/"
                    : (pathname === lp || pathname.startsWith(lp + "/"));
                link.classList.toggle("is-active", active);
            });
        } catch (_) {}
    }

    // ناوبری SPA فقط نوار پیشرفتِ نازک بالای صفحه را می‌گیرد، نه لودر لوگو.
    // لوگو مخصوص Boot/Full-reload است (ptg-loader.js). نوار پیشرفت تأخیر
    // 350ms دارد و minimum-visible ندارد؛ ناوبری سریع هیچ چیزی نشان نمی‌دهد.
    function loaderStart() {
        if (window.PTG && window.PTG.nav) window.PTG.nav.start();
    }

    function loaderDone() {
        if (window.PTG && window.PTG.nav) window.PTG.nav.done();
    }

    function fallback(url) { location.href = url; }
})();
