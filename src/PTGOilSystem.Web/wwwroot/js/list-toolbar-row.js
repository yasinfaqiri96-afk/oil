/*
 * PTG Oil System - List toolbar row
 *
 * Every list page renders three separate controls: the page action strip
 * («ثبت …») in .ak-page-header, the export menu, and the search/filter bar.
 * Views place them in different wrappers, so this cannot be lined up with CSS
 * alone. This module joins them into the single .ak-list-toolbar row that some
 * views already use: search grows on the start side, export + page actions sit
 * at the end. Display-only — no URL, form field or handler is touched, and the
 * moved nodes keep their own listeners because they are moved, not re-created.
 */

(function () {
    "use strict";

    var PAGE_SELECTOR = ".ak-list-page, .ak-form-page";
    var DONE_FLAG = "listToolbarRow";

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init, { once: true });
    } else {
        init();
    }

    window.addEventListener("ptg:page-ready", function () {
        window.requestAnimationFrame(init);
    });

    function init() {
        var pages = document.querySelectorAll(PAGE_SELECTOR);
        for (var i = 0; i < pages.length; i++) {
            mergePage(pages[i]);
        }
    }

    function mergePage(page) {
        if (page.dataset[DONE_FLAG] === "1") {
            return;
        }

        // Detail headers keep their own title/kebab contract — leave them alone.
        var pageActions = page.querySelector(".ak-page-header:not(.ak-detail-header) .ak-page-actions");
        var exportMenu = page.querySelector(".ak-export-menu");
        if (!pageActions && !exportMenu) {
            return;
        }

        var toolbar = resolveToolbar(page);
        if (!toolbar) {
            return;
        }

        var slot = actionSlot(toolbar);

        if (exportMenu) {
            adopt(slot, exportMenu);
        }
        if (pageActions) {
            adopt(slot, pageActions);
        }

        page.dataset[DONE_FLAG] = "1";
    }

    /* Find the row every control should share, in order of preference:
       the search bar's own row (wrapping it when the view has none), an
       existing .ak-list-toolbar, or a fresh row at the top of the list —
       pages without a search bar (e.g. the shipments tab) land here. */
    function resolveToolbar(page) {
        var filterHost = page.querySelector(".ak-filter-host");
        if (filterHost) {
            var parent = filterHost.parentElement;
            if (parent.classList.contains("ak-list-toolbar")) {
                return parent;
            }
            return insertToolbar(parent, filterHost, true);
        }

        var existing = page.querySelector(".ak-list-toolbar");
        if (existing) {
            return existing;
        }

        var list = page.querySelector(".ak-list");
        return list ? insertToolbar(list, list.firstChild, false) : null;
    }

    function insertToolbar(parent, before, adoptBefore) {
        var toolbar = document.createElement("div");
        toolbar.className = "ak-list-toolbar";
        parent.insertBefore(toolbar, before);
        if (adoptBefore) {
            toolbar.appendChild(before);
        }
        return toolbar;
    }

    function actionSlot(toolbar) {
        var slot = toolbar.querySelector(".ak-list-toolbar-actions");
        if (!slot) {
            slot = document.createElement("div");
            slot.className = "ak-list-toolbar-actions no-print";
            toolbar.appendChild(slot);
        }
        return slot;
    }

    /* Move the control into the row and drop the wrapper it leaves behind when
       that wrapper existed only to position it. */
    function adopt(slot, node) {
        var origin = node.parentElement;
        slot.appendChild(node);

        if (origin && origin !== slot && !origin.children.length && isDisposable(origin)) {
            origin.remove();
        }
    }

    /* Only the positioning <div> some views wrap the export menu in. The now
       empty .ak-page-header stays: CSS already hides a header without actions,
       and the page-order rules key off it still being there. */
    function isDisposable(element) {
        return element.tagName === "DIV"
            && !element.id
            && element.classList.contains("d-flex");
    }
})();
