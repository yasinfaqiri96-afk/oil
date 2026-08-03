(function () {
    "use strict";

    var tabCache = new Map();
    var pendingTabs = new Map();

    function ready(callback) {
        if (document.readyState === "loading") {
            document.addEventListener("DOMContentLoaded", callback, { once: true });
        } else {
            callback();
        }
    }

    function init() {
        window.PTG = window.PTG || {};
        if (window.PTG.contractJourneyTabsReady === true) {
            if (typeof window.PTG.refreshContractJourneyTabs === "function") {
                window.PTG.refreshContractJourneyTabs();
            }
            return;
        }

        window.PTG.contractJourneyTabsReady = true;
        window.PTG.refreshContractJourneyTabs = function () {
            cacheCurrentTab();
        };
        window.PTG.reloadContractJourneyTab = function (url) {
            var targetUrl = url || location.href;
            invalidateContractTab(targetUrl);
            return loadTab(targetUrl, false, true);
        };

        document.addEventListener("click", onTabClick, true);
        // Warm the cache before the click so tabs open instantly, but only for
        // the tab the user is actually reaching for: hover / keyboard focus.
        // NOTE: blanket idle warming of *every* tab was removed. Each tab is a
        // full server render (~26 DB queries); pre-fetching all tabs on every
        // contract open cost ~260 queries/visit and saturated the database,
        // which slowed the whole app. Targeted prefetch keeps tabs snappy
        // without the background load.
        document.addEventListener("pointerover", onTabPrefetch);
        document.addEventListener("focusin", onTabPrefetch);
        window.PTG.refreshContractJourneyTabs();
    }

    function prefetchUrlFromLink(link) {
        if (!link || !document.querySelector("[data-contract-journey-page]")) return "";
        var href = link.getAttribute("href") || "";
        if (!href || href === "#" || href.indexOf("javascript:") === 0) return "";
        try {
            var url = new URL(href, location.href);
            if (url.origin !== location.origin) return "";
            return url.href;
        } catch (_) {
            return "";
        }
    }

    function onTabPrefetch(event) {
        var target = event.target;
        var link = target && target.closest ? target.closest("[data-contract-journey-tab-link]") : null;
        var href = prefetchUrlFromLink(link);
        if (!href) return;
        prefetchTab(href).catch(function () { /* click path will surface any error */ });
    }

    function onTabClick(event) {
        if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;

        var link = event.target.closest("[data-contract-journey-tab-link]");
        if (!link || !document.querySelector("[data-contract-journey-page]")) return;

        var href = link.getAttribute("href") || "";
        if (!href || href === "#" || href.indexOf("javascript:") === 0) return;

        var url;
        try {
            url = new URL(href, location.href);
        } catch (_) {
            return;
        }

        if (url.origin !== location.origin) return;

        event.preventDefault();
        event.stopPropagation();
        loadTab(url.href, true);
    }

    function loadTab(url, pushState, forceReload) {
        var key = cacheKey(url);
        if (!key) return;

        var cached = forceReload ? null : tabCache.get(key);
        if (cached) {
            applyTab(cached, url, pushState);
            return;
        }

        setLoading(true);
        prefetchTab(url, forceReload)
            .then(function (parsed) {
                setLoading(false);
                applyTab(parsed, url, pushState);
            })
            .catch(function () {
                setLoading(false);
                showError();
            });
    }

    function prefetchTab(url, forceReload) {
        var key = cacheKey(url);
        if (!key) return Promise.reject(new Error("Invalid tab URL"));

        if (forceReload) {
            tabCache.delete(key);
        }

        var cached = tabCache.get(key);
        if (cached) return Promise.resolve(cached);

        var pending = pendingTabs.get(key);
        if (pending) return pending;

        var request = fetch(url, {
            method: "GET",
            credentials: "same-origin",
            headers: {
                "X-Requested-With": "XMLHttpRequest",
                "X-PTG-SPA": "1",
                "X-PTG-Contract-Tab": "1"
            }
        })
            .then(function (response) {
                if (!response.ok) throw new Error("Tab request failed");
                return response.text();
            })
            .then(function (html) {
                var parsed = parseTabResponse(html);
                tabCache.set(key, parsed);
                return parsed;
            })
            .finally(function () {
                pendingTabs.delete(key);
            });

        pendingTabs.set(key, request);
        return request;
    }

    function cacheCurrentTab() {
        var content = document.querySelector("[data-contract-journey-tab-content]");
        var nav = document.querySelector("[data-contract-journey-tab-nav]");
        var page = document.querySelector("[data-contract-journey-page]");
        if (!content || !nav || !page) return;

        var key = cacheKey(location.href);
        if (!key || tabCache.has(key)) return;

        var factsHost = document.querySelector("[data-contract-journey-facts-host]");

        tabCache.set(key, {
            title: document.title,
            contentHtml: content.innerHTML,
            contentAttributes: readAttributes(content),
            navHtml: nav.innerHTML,
            pageClassName: page.className,
            factsHostHtml: factsHost ? factsHost.innerHTML : null
        });
    }

    function parseTabResponse(html) {
        var doc = new DOMParser().parseFromString(html, "text/html");
        var content = doc.querySelector("[data-contract-journey-tab-content]");
        var nav = doc.querySelector("[data-contract-journey-tab-nav]");
        var page = doc.querySelector("[data-contract-journey-page]");
        var factsHost = doc.querySelector("[data-contract-journey-facts-host]");

        if (!content || !nav || !page) {
            throw new Error("Contract journey tab fragment not found");
        }

        return {
            title: doc.title || document.title,
            contentHtml: content.innerHTML,
            contentAttributes: readAttributes(content),
            navHtml: nav.innerHTML,
            pageClassName: page.className,
            factsHostHtml: factsHost ? factsHost.innerHTML : null
        };
    }

    function applyTab(parsed, url, pushState) {
        var content = document.querySelector("[data-contract-journey-tab-content]");
        var nav = document.querySelector("[data-contract-journey-tab-nav]");
        var page = document.querySelector("[data-contract-journey-page]");

        if (!content || !nav || !page) {
            location.href = url;
            return;
        }

        content.innerHTML = parsed.contentHtml;
        writeAttributes(content, parsed.contentAttributes);
        nav.innerHTML = parsed.navHtml;
        page.className = parsed.pageClassName;
        document.title = parsed.title;

        var factsHost = document.querySelector("[data-contract-journey-facts-host]");
        if (factsHost && parsed.factsHostHtml != null) {
            factsHost.innerHTML = parsed.factsHostHtml;
        }

        if (pushState) {
            history.pushState({ ptgSpa: true }, parsed.title, url);
        }

        if (typeof window.__ptgReinit === "function") {
            window.__ptgReinit();
        }

        window.dispatchEvent(new CustomEvent("ptg:page-ready", {
            detail: { url: url, source: "contract-journey-tabs" }
        }));
    }

    function setLoading(isLoading) {
        var content = document.querySelector("[data-contract-journey-tab-content]");
        if (!content) return;

        content.setAttribute("aria-busy", isLoading ? "true" : "false");

        var existing = content.querySelector("[data-contract-journey-tab-loading]");
        if (existing) existing.remove();

        if (!isLoading) return;

        var loading = document.createElement("div");
        loading.className = "visually-hidden";
        loading.setAttribute("data-contract-journey-tab-loading", "true");
        loading.setAttribute("aria-live", "polite");
        loading.textContent = content.getAttribute("data-loading-text") || "Loading tab...";
        content.prepend(loading);
    }

    function showError() {
        var content = document.querySelector("[data-contract-journey-tab-content]");
        if (!content) return;

        var old = content.querySelector("[data-contract-journey-tab-error]");
        if (old) old.remove();

        var error = document.createElement("div");
        error.className = "alert alert-warning border mb-3";
        error.setAttribute("data-contract-journey-tab-error", "true");
        error.textContent = content.getAttribute("data-error-text") || "Could not load this tab. Try again.";
        content.prepend(error);
    }

    function cacheKey(url) {
        try {
            var parsed = new URL(url, location.href);
            parsed.hash = "";
            return parsed.pathname + parsed.search;
        } catch (_) {
            return "";
        }
    }

    function invalidateContractTab(url) {
        var key = cacheKey(url);
        if (key) tabCache.delete(key);
    }

    function readAttributes(element) {
        var attributes = {};
        Array.prototype.slice.call(element.attributes).forEach(function (attribute) {
            attributes[attribute.name] = attribute.value;
        });
        return attributes;
    }

    function writeAttributes(element, attributes) {
        Array.prototype.slice.call(element.attributes).forEach(function (attribute) {
            element.removeAttribute(attribute.name);
        });
        Object.keys(attributes || {}).forEach(function (name) {
            element.setAttribute(name, attributes[name]);
        });
    }

    ready(init);
})();
