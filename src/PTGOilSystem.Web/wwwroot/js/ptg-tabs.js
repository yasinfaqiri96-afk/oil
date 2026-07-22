/*
 * ptg-tabs.js — the single tab engine for every .ptg-tabs-rail in the system.
 *
 * A rail is one of two kinds, decided per tab:
 *
 *   panel tab  — points at an in-page panel (href="#id", data-ptg-tab-target,
 *                or the legacy data-bs-target). The engine owns activation:
 *                it shows one panel, hides the rest, and mirrors the choice
 *                into the URL hash so a refresh restores the same tab.
 *   link  tab  — points at a real route (_SectionTabs, ContractJourney,
 *                Drivers…). The engine never intercepts the click; the server
 *                decides which tab is active. Bookmarkable, as before.
 *
 * Either way the engine adds the shared behaviour: ARIA wiring, roving-focus
 * keyboard navigation, and keeping the active tab inside the visible scroll
 * area on small screens.
 *
 * Rails driven by their own page script (e.g. [data-ptcd-tab]) resolve no
 * panel target, so they fall through to link-tab handling: the engine gives
 * them keyboard + scroll only and leaves their activation logic alone.
 */
(function () {
    "use strict";

    var RAIL = ".ptg-tabs-rail";
    var TAB = ".ptg-tab-item";
    var SCROLL_STEP = 0.7; // fraction of the visible width per arrow click

    function isDisabled(tab) {
        return tab.disabled === true
            || tab.classList.contains("is-disabled")
            || tab.getAttribute("aria-disabled") === "true";
    }

    // href="#x" wins, then the explicit hook, then Bootstrap's old attribute.
    function panelIdOf(tab) {
        var href = tab.getAttribute("href") || "";
        var target = tab.getAttribute("data-ptg-tab-target")
            || tab.getAttribute("data-bs-target")
            || "";
        var raw = href.charAt(0) === "#" ? href : target;
        return raw.charAt(0) === "#" && raw.length > 1 ? raw.slice(1) : null;
    }

    // Only interactive elements are real tabs. Some wizards reuse the rail for
    // non-interactive step chips (<span class="ptg-tab-item">); those keep the
    // look but must not be announced as a tablist or take keyboard focus.
    function tabsOf(rail) {
        return Array.prototype.slice.call(rail.querySelectorAll(TAB)).filter(function (tab) {
            return tab.tagName === "A" || tab.tagName === "BUTTON";
        });
    }

    // Keep the active tab in view without yanking the whole page around.
    function revealTab(rail, tab) {
        var scroller = rail.closest("[data-section-tabs-scroll]") || rail;
        if (scroller.scrollWidth <= scroller.clientWidth + 2) {
            return;
        }

        var tabBox = tab.getBoundingClientRect();
        var box = scroller.getBoundingClientRect();
        if (tabBox.left >= box.left && tabBox.right <= box.right) {
            return;
        }

        var delta = tabBox.left < box.left
            ? tabBox.left - box.left - 16
            : tabBox.right - box.right + 16;
        scroller.scrollBy({ left: delta, behavior: "smooth" });
    }

    /* ---------------- panel rails ---------------- */

    function wirePanels(rail, entries) {
        function activate(id, animate) {
            entries.forEach(function (entry) {
                var on = entry.id === id;

                entry.tab.classList.toggle("is-active", on);
                entry.tab.classList.toggle("active", on);
                entry.tab.setAttribute("aria-selected", on ? "true" : "false");
                entry.tab.tabIndex = on ? 0 : -1;

                entry.panel.hidden = !on;
                if (entry.panel.classList.contains("tab-pane")) {
                    entry.panel.classList.toggle("show", on);
                    entry.panel.classList.toggle("active", on);
                }
                if (entry.panel.tagName === "DETAILS") {
                    entry.panel.open = on;
                }

                // Replay the fade only on the panel being opened.
                entry.panel.classList.remove("ptg-tab-enter");
                if (on && animate) {
                    void entry.panel.offsetWidth;
                    entry.panel.classList.add("ptg-tab-enter");
                }
            });

            // Chunks that live outside the panel but belong to one tab.
            Array.prototype.slice.call(document.querySelectorAll("[data-ak-tab]")).forEach(function (el) {
                var owner = el.getAttribute("data-ak-tab");
                if (!owner || !entries.some(function (e) { return e.id === owner; })) {
                    return;
                }
                var show = owner === id;
                el.hidden = !show;
                el.classList.remove("ptg-tab-enter");
                if (show && animate) {
                    void el.offsetWidth;
                    el.classList.add("ptg-tab-enter");
                }
            });
        }

        rail.addEventListener("click", function (event) {
            var tab = event.target.closest(TAB);
            if (!tab || !rail.contains(tab) || isDisabled(tab)) {
                return;
            }
            var id = panelIdOf(tab);
            if (!id || !entries.some(function (e) { return e.id === id; })) {
                return;
            }

            event.preventDefault();
            // Dense operational pages can opt out of the decorative fade. Their
            // panels are already in the DOM, so the switch is truly immediate.
            var animate = !rail.closest("[data-instant-tabs]");
            activate(id, animate);
            revealTab(rail, tab);

            if (history.replaceState) {
                // Only the hash changes. Preserve the existing state object —
                // spa-nav.js keys off state.ptgSpa, and nulling it breaks Back.
                history.replaceState(history.state || { ptgSpa: true }, "", "#" + id);
            }
        });

        // Restore order: URL hash, then the server's active tab, then the first.
        var hashId = (location.hash || "").slice(1);
        var initial = entries.filter(function (e) { return e.id === hashId; })[0]
            || entries.filter(function (e) {
                return e.tab.classList.contains("is-active") || e.tab.classList.contains("active");
            })[0]
            || entries[0];

        activate(initial.id, false); // no animation on first paint — avoids flicker
        return initial.tab;
    }

    /* ---------------- keyboard ---------------- */

    function wireKeyboard(rail, tabs, activeTab) {
        var isRtl = (document.documentElement.getAttribute("dir") || "").toLowerCase() === "rtl"
            || getComputedStyle(rail).direction === "rtl";

        tabs.forEach(function (tab) {
            if (tab.tabIndex !== 0) {
                tab.tabIndex = tab === activeTab ? 0 : -1;
            }
        });

        rail.addEventListener("keydown", function (event) {
            var current = event.target.closest(TAB);
            if (!current || !rail.contains(current)) {
                return;
            }

            if (event.key === "Enter" || event.key === " " || event.key === "Spacebar") {
                if (isDisabled(current)) {
                    event.preventDefault();
                    return;
                }
                // Links activate natively on Enter; only Space needs forcing.
                if (event.key !== "Enter" || current.tagName !== "A") {
                    event.preventDefault();
                    current.click();
                }
                return;
            }

            var step = 0;
            // In RTL the next tab in DOM order sits to the LEFT on screen.
            if (event.key === "ArrowLeft") { step = isRtl ? 1 : -1; }
            else if (event.key === "ArrowRight") { step = isRtl ? -1 : 1; }
            else if (event.key !== "Home" && event.key !== "End") { return; }

            var enabled = tabs.filter(function (tab) { return !isDisabled(tab); });
            if (!enabled.length) {
                return;
            }

            event.preventDefault();

            var next;
            if (event.key === "Home") {
                next = enabled[0];
            } else if (event.key === "End") {
                next = enabled[enabled.length - 1];
            } else {
                var at = enabled.indexOf(current);
                if (at === -1) {
                    return;
                }
                next = enabled[(at + step + enabled.length) % enabled.length];
            }

            tabs.forEach(function (tab) { tab.tabIndex = -1; });
            next.tabIndex = 0;
            next.focus();
            revealTab(rail, next);
        });
    }

    /* ---------------- scroll shell ---------------- */

    function wireHorizontalScroll(rail) {
        if (rail.dataset.horizontalScrollReady === "true") {
            return;
        }
        rail.dataset.horizontalScrollReady = "true";

        var scroller = rail.closest("[data-section-tabs-scroll]") || rail;

        // Turn vertical wheel into horizontal travel while over the rail.
        scroller.addEventListener("wheel", function (event) {
            if (scroller.scrollWidth <= scroller.clientWidth + 2) { return; }
            if (Math.abs(event.deltaY) <= Math.abs(event.deltaX)) { return; }

            event.preventDefault();
            scroller.scrollBy({ left: event.deltaY, behavior: "auto" });
        }, { passive: false });
    }

    function wireArrows(shell) {
        if (!shell || shell.dataset.sectionTabsReady === "true") {
            return;
        }

        var scroll = shell.querySelector("[data-section-tabs-scroll]");
        if (!scroll) {
            return;
        }
        shell.dataset.sectionTabsReady = "true";

        var prev = shell.querySelector('[data-section-tabs-arrow="prev"]');
        var next = shell.querySelector('[data-section-tabs-arrow="next"]');

        function update() {
            var overflow = scroll.scrollWidth - scroll.clientWidth > 2;
            if (prev) { prev.hidden = !overflow; }
            if (next) { next.hidden = !overflow; }
        }

        function step(dir) {
            var amount = Math.max(120, Math.round(scroll.clientWidth * SCROLL_STEP));
            scroll.scrollBy({ left: dir * amount, behavior: "smooth" });
        }

        if (prev) { prev.addEventListener("click", function () { step(-1); }); }
        if (next) { next.addEventListener("click", function () { step(1); }); }
        window.addEventListener("resize", update);
        update();
    }

    /* ---------------- boot ---------------- */

    function wireRail(rail) {
        if (rail.dataset.ptgTabsReady === "true") {
            return;
        }
        rail.dataset.ptgTabsReady = "true";

        var tabs = tabsOf(rail);
        if (!tabs.length) {
            return;
        }

        if (!rail.hasAttribute("role")) {
            rail.setAttribute("role", "tablist");
        }

        var entries = [];
        tabs.forEach(function (tab) {
            if (!tab.hasAttribute("role")) {
                tab.setAttribute("role", "tab");
            }
            // Reserve the active (600) text metrics so activation cannot reflow
            // the rail; the CSS renders this in a zero-height ::after.
            var label = tab.querySelector("span:not(.ptg-tab-badge):not(.ak-status)");
            if (label && !label.hasAttribute("data-tab-label")) {
                label.setAttribute("data-tab-label", (label.textContent || "").trim());
            }

            var id = panelIdOf(tab);
            var panel = id ? document.getElementById(id) : null;
            if (!panel) {
                return;
            }

            if (!panel.hasAttribute("role")) {
                panel.setAttribute("role", "tabpanel");
            }
            panel.setAttribute("data-ptg-tab-panel", "");
            tab.setAttribute("aria-controls", id);
            if (!panel.getAttribute("aria-label") && !panel.getAttribute("aria-labelledby")) {
                if (!tab.id) {
                    tab.id = "ptg-tab-" + id;
                }
                panel.setAttribute("aria-labelledby", tab.id);
            }

            entries.push({ tab: tab, panel: panel, id: id });
        });

        // One rail must not be half panel-driven and half route-driven.
        var activeTab = entries.length >= 2 ? wirePanels(rail, entries) : null;

        if (!activeTab) {
            activeTab = tabs.filter(function (tab) {
                return tab.classList.contains("is-active")
                    || tab.classList.contains("active")
                    || tab.getAttribute("aria-current") === "page";
            })[0] || tabs[0];
        }

        wireKeyboard(rail, tabs, activeTab);
        wireHorizontalScroll(rail);
        revealTab(rail, activeTab);
    }

    function init() {
        Array.prototype.slice.call(document.querySelectorAll(RAIL)).forEach(wireRail);
        Array.prototype.slice.call(document.querySelectorAll('[data-section-tabs="true"]')).forEach(wireArrows);
    }

    // Pages that need to open a tab themselves (e.g. ?tab=profit deep links)
    // click the tab rather than reaching into the engine's internals.
    window.ptgTabs = {
        show: function (panelId) {
            var id = String(panelId || "").replace(/^#/, "");
            if (!id) {
                return false;
            }
            var tab = Array.prototype.slice.call(document.querySelectorAll(TAB)).filter(function (candidate) {
                return panelIdOf(candidate) === id;
            })[0];
            if (!tab || isDisabled(tab)) {
                return false;
            }
            tab.click();
            return true;
        }
    };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init, { once: true });
    } else {
        init();
    }

    window.addEventListener("ptg:page-ready", init);
})();
