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
        var isListSurface = page.classList.contains("ak-list-page");
        var pageActions = page.querySelector(".ak-page-header:not(.ak-detail-header) .ak-page-actions");
        var exportMenu = page.querySelector(".ak-export-menu");
        var hasToolbarContent = pageActions
            || exportMenu
            || page.querySelector(".ak-filter-host, .ak-list-toolbar");
        if (!hasToolbarContent && !isListSurface) {
            return;
        }

        var toolbar = hasToolbarContent ? resolveToolbar(page) : null;
        if (toolbar && (exportMenu || pageActions)) {
            var slot = actionSlot(toolbar);
            if (exportMenu) {
                adopt(slot, exportMenu);
            }
            if (pageActions) {
                adopt(slot, pageActions);
            }
        }

        if (isListSurface) {
            assembleListCard(page, toolbar);
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

    /*
     * Group the existing toolbar, table and pager into one visual card. Nodes
     * are moved, not rebuilt, so their links, forms, listeners and permissions
     * stay exactly as rendered by each view.
     */
    function assembleListCard(page, toolbar) {
        var tables = page.querySelectorAll("table.ak-table");
        Array.prototype.forEach.call(tables, function (table, index) {
            if (!table.closest(".ak-list-card")) {
                assembleTableCard(page, table, index === 0 ? toolbar : null);
            }
        });
    }

    function assembleTableCard(page, table, toolbar) {
        var card = table.closest(".ak-list");
        var tableShell = outerTableShell(table, card);

        if (!card || !page.contains(card)) {
            card = document.createElement("section");
            card.className = "ak-list";
            tableShell.parentElement.insertBefore(card, tableShell);
            card.appendChild(tableShell);
        }

        card.classList.add("ak-list-card");
        card.dataset.akListCard = "ready";

        if (toolbar && !card.contains(toolbar)) {
            card.insertBefore(toolbar, card.firstChild);
        }

        var footer = ensureListFooter(card);
        moveFooterParts(page, card, tableShell, footer);
        ensureResultCount(footer, table);
        ensureLoadingState(card);
        bindLoadingState(card, toolbar);
    }

    function outerTableShell(table, card) {
        var shell = table.closest(".ak-table-wrap") || table;
        while (shell.parentElement
            && shell.parentElement !== card
            && shell.parentElement.classList.contains("ak-table-wrap")) {
            shell = shell.parentElement;
        }
        return shell;
    }

    function ensureListFooter(card) {
        var footer = Array.prototype.find.call(card.children, function (child) {
            return child.classList && child.classList.contains("ak-list-footer");
        });
        if (!footer) {
            footer = document.createElement("footer");
            footer.className = "ak-list-footer";
            card.appendChild(footer);
        }
        return footer;
    }

    function moveFooterParts(page, card, tableShell, footer) {
        var candidates = Array.prototype.slice.call(card.children)
            .concat(Array.prototype.slice.call(page.children));
        var sibling = tableShell.nextElementSibling;
        if (sibling) {
            candidates.push(sibling);
        }

        candidates.forEach(function (node) {
            if (!node || node === footer || footer.contains(node)) return;
            if (node.matches && node.matches(".ak-pager, .ptg-client-pager, .ak-list-result-count")) {
                footer.appendChild(node);
            }
        });

        card.querySelectorAll(
            ":scope > .ak-pager, :scope > .ptg-client-pager, :scope > .ak-list-result-count"
        ).forEach(function (node) {
            if (node !== footer && !footer.contains(node)) {
                footer.appendChild(node);
            }
        });
    }

    function ensureResultCount(footer, table) {
        if (footer.querySelector(".ak-list-result-count, .ptg-client-pager-info")) {
            return;
        }

        var rows = table.tBodies && table.tBodies.length ? table.tBodies[0].rows : [];
        var count = Array.prototype.filter.call(rows, function (row) {
            return !row.querySelector(".ak-empty")
                && !Array.prototype.some.call(row.cells, function (cell) {
                    return Number(cell.getAttribute("colspan") || "1") > 1;
                });
        }).length;

        var label = document.createElement("span");
        label.className = "ak-list-result-count";
        label.setAttribute("aria-live", "polite");
        label.textContent = resultCountText(count);
        footer.insertBefore(label, footer.firstChild);
    }

    function resultCountText(count) {
        var isEnglish = (document.documentElement.lang || "").toLowerCase().startsWith("en");
        var number = count.toLocaleString(isEnglish ? "en-US" : "fa-IR");
        return isEnglish ? number + " results shown" : "نمایش " + number + " نتیجه";
    }

    function ensureLoadingState(card) {
        if (card.querySelector(":scope > .ak-list-loading-state")) {
            return;
        }

        var state = document.createElement("div");
        state.className = "ak-list-loading-state";
        state.hidden = true;
        state.setAttribute("role", "status");
        state.setAttribute("aria-live", "polite");

        var spinner = document.createElement("span");
        spinner.className = "ak-list-loading-spinner";
        spinner.setAttribute("aria-hidden", "true");

        var text = document.createElement("span");
        var isEnglish = (document.documentElement.lang || "").toLowerCase().startsWith("en");
        text.textContent = isEnglish ? "Loading results…" : "در حال بارگذاری نتایج…";

        state.appendChild(spinner);
        state.appendChild(text);
        card.appendChild(state);
    }

    function bindLoadingState(card, toolbar) {
        if (!toolbar || toolbar.dataset.akListLoadingReady === "true") {
            return;
        }
        toolbar.dataset.akListLoadingReady = "true";

        toolbar.querySelectorAll("form[method='get'], form:not([method])").forEach(function (form) {
            form.addEventListener("submit", function () {
                setListCardLoading(card, true);
            });
        });
    }

    function setListCardLoading(card, active) {
        if (!card) return;
        var state = card.querySelector(":scope > .ak-list-loading-state");
        card.classList.toggle("is-loading", active);
        card.setAttribute("aria-busy", active ? "true" : "false");
        if (state) {
            state.hidden = !active;
        }
    }

    window.addEventListener("pageshow", function () {
        document.querySelectorAll(".ak-list-card.is-loading").forEach(function (card) {
            setListCardLoading(card, false);
        });
    });

    window.PTG = window.PTG || {};
    window.PTG.setListCardLoading = setListCardLoading;
})();
