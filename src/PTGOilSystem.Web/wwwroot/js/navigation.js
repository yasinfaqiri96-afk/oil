/*
 * PTG Oil System - Navigation Module
 * Handles shell navigation, sidebar collapse, and menu interactions
 */

(function () {
    "use strict";

    var expandedSidebarMedia = window.matchMedia("(min-width: 1200px)");
    var persistentSidebarMedia = window.matchMedia("(min-width: 992px)");

    // Auto-initialize
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init, { once: true });
    } else {
        init();
    }

    function init() {
        initializeShellNavigation();
    }

    function initializeShellNavigation() {
        if (!document.body || document.body.dataset.shellReady === "true") return;

        applySidebarMode();

        // Shell toggle buttons (hamburger menu)
        document.querySelectorAll("[data-shell-toggle='true']").forEach(function (button) {
            if (button.dataset.shellToggleReady === "true") return;
            button.dataset.shellToggleReady = "true";

            button.addEventListener("click", function () {
                if (expandedSidebarMedia.matches) {
                    document.body.classList.toggle("is-sidebar-collapsed");
                    try {
                        localStorage.setItem("ptg-sidebar-collapsed",
                            document.body.classList.contains("is-sidebar-collapsed") ? "1" : "0"
                        );
                    } catch (_) {}
                } else {
                    document.body.classList.toggle("is-shell-nav-open");
                    syncSidebarToggleState();
                }
            });
        });

        // Shell close buttons
        document.querySelectorAll("[data-shell-close='true']").forEach(function (button) {
            if (button.dataset.shellCloseReady === "true") return;
            button.dataset.shellCloseReady = "true";

            button.addEventListener("click", closeShellNavigation);
        });

        document.querySelectorAll("[data-nav-group-toggle='true']").forEach(function (button) {
            if (button.dataset.navGroupReady === "true") return;
            button.dataset.navGroupReady = "true";

            button.addEventListener("click", function () {
                // Expand the compact rail before opening a labeled submenu.
                if (expandedSidebarMedia.matches && document.body.classList.contains("is-sidebar-collapsed")) {
                    document.body.classList.remove("is-sidebar-collapsed");
                    try {
                        localStorage.setItem("ptg-sidebar-collapsed", "0");
                    } catch (_) {}
                }

                if (persistentSidebarMedia.matches && !expandedSidebarMedia.matches) {
                    document.body.classList.add("is-shell-nav-open");
                    syncSidebarToggleState();
                }

                var group = button.closest("[data-nav-group]");
                if (!group) return;

                var isOpen = group.classList.toggle("is-open");
                button.setAttribute("aria-expanded", String(isOpen));

                // Accordion: only one group open per level — opening one closes its siblings.
                if (isOpen && group.parentElement) {
                    group.parentElement.querySelectorAll(":scope > [data-nav-group].is-open").forEach(function (sibling) {
                        if (sibling === group) return;
                        sibling.classList.remove("is-open");
                        var siblingToggle = sibling.querySelector(":scope > [data-nav-group-toggle='true']");
                        if (siblingToggle) siblingToggle.setAttribute("aria-expanded", "false");
                    });
                }
            });
        });

        window.PTG = window.PTG || {};
        if (window.PTG.shellResizeReady !== true) {
            window.PTG.shellResizeReady = true;
            window.addEventListener("resize", function () {
                applySidebarMode();
            }, { passive: true });
        }

        // Minimal header: toggle a translucent-blur state once the page
        // scrolls under the sticky topbar. The scroll container is .ptg-app
        // (not window); it persists across SPA nav, so bind once.
        if (window.PTG.topbarScrollReady !== true) {
            var scroller = document.querySelector(".ptg-app");
            if (scroller) {
                window.PTG.topbarScrollReady = true;
                var applyScrolled = function () {
                    document.body.classList.toggle("is-topbar-scrolled", scroller.scrollTop > 4);
                };
                scroller.addEventListener("scroll", applyScrolled, { passive: true });
                applyScrolled();
            }
        }

        syncSidebarToggleState();
        document.body.dataset.shellReady = "true";
    }

    function closeShellNavigation() {
        if (!document.body) return;
        document.body.classList.remove("is-shell-nav-open");
        syncSidebarToggleState();
    }

    function applySidebarMode() {
        if (!document.body) return;

        closeShellNavigation();
        if (!expandedSidebarMedia.matches) {
            document.body.classList.remove("is-sidebar-collapsed");
            return;
        }

        try {
            document.body.classList.toggle(
                "is-sidebar-collapsed",
                localStorage.getItem("ptg-sidebar-collapsed") === "1"
            );
        } catch (_) {
            document.body.classList.remove("is-sidebar-collapsed");
        }
    }

    function syncSidebarToggleState() {
        var isOpen = document.body.classList.contains("is-shell-nav-open");
        document.querySelectorAll("[data-shell-toggle='true']").forEach(function (button) {
            button.setAttribute("aria-expanded", String(isOpen));
        });
    }

    // Expose to global scope
    window.PTG = window.PTG || {};
    window.PTG.closeShellNavigation = closeShellNavigation;
    window.PTG.initializeShellNavigation = initializeShellNavigation;

})();
