/*
 * PTG Oil System - Core Module
 * Extracted from site.js for better organization
 */

(function () {
    "use strict";

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initializeShell, { once: true });
    } else {
        initializeShell();
    }

    // SPA navigation replaces <main>.innerHTML without a page reload, so
    // DOMContentLoaded never fires again. Re-run the shared initializer on the
    // canonical ptg:page-ready signal (same hook tables.js/ptg-tabs.js use) so
    // freshly-swapped content — e.g. the bulk-receipt toggle — gets wired up.
    // Every initializer here is idempotent (per-element/per-body *Ready guards),
    // so re-running never double-binds listeners on surviving elements.
    window.addEventListener("ptg:page-ready", initializeShell);

    function initializeShell() {
        callIfAvailable("initializeLanguageSwitcher");
        initializeFlashAlerts();
        callIfAvailable("initializeShellNavigation");
        callIfAvailable("initializeResponsiveTables");
        initializeTableActionMenus();
        initializePageModalLinks();
        callIfAvailable("initializeClickableTableRows");
        initializeActivityTabs();
        initializeBulkReceiptForms();
        initializeSubmitGuard();
        callIfAvailable("initializeFinanceForms");
    }

    function callIfAvailable(functionName) {
        if (window.PTG && typeof window.PTG[functionName] === "function") {
            window.PTG[functionName]();
            return;
        }

        if (typeof window[functionName] === "function") {
            window[functionName]();
        }
    }

    // Flash Alerts
    function initializeFlashAlerts() {
        document.querySelectorAll("[data-boltz-auto-dismiss]").forEach(function (alert) {
            if (alert.dataset.boltzAutoDismissReady === "true") return;
            alert.dataset.boltzAutoDismissReady = "true";

            var delay = parseInt(alert.getAttribute("data-boltz-auto-dismiss"), 10);
            if (!Number.isFinite(delay) || delay < 1000) delay = 4200;

            window.setTimeout(function () {
                dismissFlashAlert(alert);
            }, delay);
        });
    }

    function dismissFlashAlert(alert) {
        if (!alert || !alert.isConnected || alert.classList.contains("is-dismissing")) return;

        alert.classList.add("is-dismissing");
        window.setTimeout(function () {
            if (!alert.isConnected) return;
            var stack = alert.parentElement;
            alert.remove();
            if (stack && !stack.children.length) stack.remove();
        }, 180);
    }

    // Activity Tabs
    function initializeActivityTabs() {
        document.querySelectorAll(".boltz-activity-tabs").forEach(function (tabGroup) {
            if (tabGroup.dataset.activityTabsReady === "true") return;
            tabGroup.dataset.activityTabsReady = "true";

            tabGroup.addEventListener("click", function (event) {
                var tab = event.target.closest(".boltz-activity-tab");
                if (!tab) return;

                tabGroup.querySelectorAll(".boltz-activity-tab").forEach(function (item) {
                    item.classList.remove("is-active");
                });
                tab.classList.add("is-active");
            });
        });
    }

    // Table Action Menus
    function initializeTableActionMenus() {
        document.querySelectorAll(".table .dropdown-toggle").forEach(function (toggle) {
            if (toggle.dataset.actionMenuReady === "true") return;
            toggle.dataset.actionMenuReady = "true";

            toggle.addEventListener("click", function (event) {
                event.stopPropagation();
            });
        });
    }

    // مانند submitGuard: پرچم باید در ماژول بماند نه روی body، چون ناوبری SPA
    // صفت‌های data-* بدنه را با صفحهٔ تازه هم‌تراز می‌کند و پرچم پاک می‌شود؛
    // آن‌گاه هر ناوبری یک شنوندهٔ click تازه روی document اضافه می‌کند و
    // لینک‌های page-modal چند بار باز می‌شوند.
    var pageModalReady = false;

    function initializePageModalLinks() {
        if (pageModalReady) return;
        pageModalReady = true;

        document.addEventListener("click", function (event) {
            var link = event.target.closest("a[data-page-modal]");
            if (!link || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
                return;
            }

            event.preventDefault();
            openPageModal(link.href, {
                title: link.getAttribute("data-page-modal-title") || link.textContent || "فرم عملیاتی",
                closeOnRedirect: link.hasAttribute("data-page-modal-close-on-redirect"),
                size: link.getAttribute("data-page-modal-size") || ""
            });
        });

        var modalElement = document.getElementById("ptgPageModal");
        if (modalElement) {
            modalElement.addEventListener("hidden.bs.modal", function () {
                var frame = modalElement.querySelector("[data-page-modal-frame]");
                if (frame) frame.setAttribute("src", "about:blank");
            });
        }
    }

    function openPageModal(url, options) {
        var modalElement = document.getElementById("ptgPageModal");
        if (!modalElement || !window.bootstrap) {
            window.location.href = url;
            return;
        }

        var frame = modalElement.querySelector("[data-page-modal-frame]");
        var title = modalElement.querySelector("#ptgPageModalLabel");
        var loadingIndicator = modalElement.querySelector("[data-page-modal-loading]");
        var modalUrl = new URL(url, window.location.origin);
        modalUrl.searchParams.set("modal", "1");

        if (title) {
            title.textContent = (options && options.title ? options.title : "فرم عملیاتی").trim();
        }

        // Opt-in compact sizing: a link may pass data-page-modal-size="compact"
        // to shrink the shared page-modal for small forms (e.g. contract pricing).
        modalElement.classList.toggle("is-compact", !!(options && options.size === "compact"));

        // Show a loading indicator immediately so the modal feels instant while the
        // iframe document (styles + scripts) finishes loading, then hide it on load.
        if (loadingIndicator) {
            loadingIndicator.hidden = false;
        }
        if (frame) {
            frame.classList.add("is-loading");
            var hideLoading = function () {
                if (loadingIndicator) loadingIndicator.hidden = true;
                frame.classList.remove("is-loading");
            };
            if (frame._ptgModalHideLoading) {
                frame.removeEventListener("load", frame._ptgModalHideLoading);
            }
            frame._ptgModalHideLoading = hideLoading;
            frame.addEventListener("load", hideLoading);
        }

        if (frame) {
            // Drop any stale close-on-redirect handler from a previous open so
            // handlers never stack across modal opens.
            if (frame._ptgModalLoadHandler) {
                frame.removeEventListener("load", frame._ptgModalLoadHandler);
                frame._ptgModalLoadHandler = null;
            }

            // Opt-in bridge: small edit forms opened in the modal iframe redirect
            // to their return page on a valid save. When the iframe navigates away
            // from the modal form (i.e. the document is no longer a modal layout),
            // close the modal and take the parent to that page. Front-end only —
            // no controller/POST change; validation re-renders stay in the modal
            // because they keep the ptg-modal-document layout.
            if (options && options.closeOnRedirect) {
                var sawModalForm = false;
                var onFrameLoad = function () {
                    try {
                        var doc = frame.contentDocument;
                        if (!doc || !doc.body) return;

                        if (doc.body.classList.contains("ptg-modal-document")) {
                            sawModalForm = true;
                            return;
                        }

                        // Ignore the blank reset and anything before the form loaded.
                        var completedUrl = frame.contentWindow.location.href;
                        if (!completedUrl || completedUrl === "about:blank" || !sawModalForm) {
                            return;
                        }

                        frame.removeEventListener("load", onFrameLoad);
                        frame._ptgModalLoadHandler = null;
                        closePageModal();

                        if (window.PTG && typeof window.PTG.spaNavigate === "function") {
                            window.PTG.spaNavigate(completedUrl);
                        } else {
                            window.location.assign(completedUrl);
                        }
                    } catch (error) {
                        // Cross-origin / detached frame — leave the modal untouched.
                    }
                };

                frame._ptgModalLoadHandler = onFrameLoad;
                frame.addEventListener("load", onFrameLoad);
            }

            frame.setAttribute("src", modalUrl.toString());
        }

        window.bootstrap.Modal.getOrCreateInstance(modalElement).show();
    }

    function closePageModal(options) {
        var modalElement = document.getElementById("ptgPageModal");
        var redirectUrl = options && options.redirectUrl ? options.redirectUrl : null;

        if (modalElement && window.bootstrap) {
            window.bootstrap.Modal.getOrCreateInstance(modalElement).hide();
        }

        if (redirectUrl) {
            window.location.href = redirectUrl;
        }
    }

    function initializeBulkReceiptForms() {
        document.querySelectorAll("[data-bulk-receipt-form]").forEach(function (form) {
            if (form.dataset.bulkReceiptReady === "true") return;
            form.dataset.bulkReceiptReady = "true";

            var rows = Array.prototype.slice.call(form.querySelectorAll("[data-bulk-receipt-row]"));
            var selectedCount = form.querySelector("[data-bulk-receipt-selected-count]");
            var selectedQty = form.querySelector("[data-bulk-receipt-selected-qty]");
            var totalInput = form.querySelector("[data-bulk-receipt-total-input]");
            var selectAll = form.querySelector("[data-bulk-receipt-select-all]");
            var clearAll = form.querySelector("[data-bulk-receipt-clear]");
            var toggle = form.querySelector("[data-bulk-receipt-toggle]");
            var panel = form.querySelector("[data-bulk-receipt-panel]");
            var toggleLabel = form.querySelector("[data-bulk-receipt-toggle-label]");
            var terminalSelect = form.querySelector("[data-bulk-receipt-terminal-select]");
            var tankSelect = form.querySelector("[data-bulk-receipt-tank-select]");

            function formatQty(value) {
                return new Intl.NumberFormat("en-US", {
                    minimumFractionDigits: 4,
                    maximumFractionDigits: 4
                }).format(value);
            }

            function readQty(row) {
                return Number.parseFloat(row.getAttribute("data-bulk-receipt-qty") || "0") || 0;
            }

            function syncStorageTankOptions() {
                if (!terminalSelect || !tankSelect) return;

                var selectedTerminalId = terminalSelect.value || "";
                var selectedTankStillVisible = false;

                Array.prototype.forEach.call(tankSelect.options, function (option) {
                    if (!option.value) {
                        option.hidden = false;
                        option.disabled = false;
                        return;
                    }

                    var optionTerminalId = option.getAttribute("data-terminal-id") || "";
                    var matchesTerminal = selectedTerminalId !== "" && optionTerminalId === selectedTerminalId;

                    option.hidden = !matchesTerminal;
                    option.disabled = !matchesTerminal;

                    if (matchesTerminal && option.selected) {
                        selectedTankStillVisible = true;
                    }
                });

                if (!selectedTankStillVisible) {
                    tankSelect.value = "";
                }
            }

            function syncSummary(updateInput) {
                var checked = rows.filter(function (row) { return row.checked; });
                var total = checked.reduce(function (sum, row) { return sum + readQty(row); }, 0);

                if (selectedCount) selectedCount.textContent = String(checked.length);
                if (selectedQty) selectedQty.textContent = formatQty(total);
                if (updateInput && totalInput) totalInput.value = total.toFixed(4).replace(/\.?0+$/, "");
            }

            function setBulkReceiptPanelOpen(isOpen) {
                if (!panel || !toggle) return;

                panel.hidden = !isOpen;
                form.classList.toggle("is-bulk-receipt-open", isOpen);
                toggle.setAttribute("aria-expanded", String(isOpen));

                var icon = toggle.querySelector("i");
                if (icon) {
                    icon.classList.toggle("bi-chevron-down", !isOpen);
                    icon.classList.toggle("bi-chevron-up", isOpen);
                }

                if (toggleLabel) {
                    toggleLabel.textContent = isOpen
                        ? (toggleLabel.getAttribute("data-close-label") || "Close form")
                        : (toggleLabel.getAttribute("data-open-label") || "Open form");
                }
            }

            function findBulkReceiptRowFromPoint(clientX, clientY) {
                var element = document.elementFromPoint(clientX, clientY);
                if (!element || !form.contains(element)) return null;

                var directRow = element.closest("[data-bulk-receipt-row]");
                if (directRow) return directRow;

                var tableRow = element.closest("tr");
                return tableRow ? tableRow.querySelector("[data-bulk-receipt-row]") : null;
            }

            function applyDragSelection(dragState, row) {
                if (!dragState || !row || dragState.visited.has(row)) return;

                dragState.visited.add(row);
                row.checked = dragState.targetChecked;
                syncSummary(true);
            }

            var dragState = null;
            var suppressClickRows = new WeakSet();

            function finishDragSelection() {
                if (!dragState) return;

                if (dragState.sourceRow && dragState.sourceRow.releasePointerCapture && dragState.pointerId !== null) {
                    try {
                        dragState.sourceRow.releasePointerCapture(dragState.pointerId);
                    } catch (_) {
                        // Pointer capture can already be released by the browser.
                    }
                }

                form.classList.remove("is-bulk-receipt-dragging");
                dragState = null;
            }

            function handleDragMove(event) {
                if (!dragState) return;

                dragState.lastClientX = event.clientX;
                dragState.lastClientY = event.clientY;
                applyDragSelection(dragState, findBulkReceiptRowFromPoint(event.clientX, event.clientY));
            }

            rows.forEach(function (row) {
                row.addEventListener("change", function () {
                    syncSummary(true);
                });

                row.addEventListener("click", function (event) {
                    if (!suppressClickRows.has(row)) return;

                    suppressClickRows.delete(row);
                    event.preventDefault();
                    event.stopPropagation();
                }, true);

                row.addEventListener("pointerdown", function (event) {
                    if (event.button !== undefined && event.button !== 0) return;

                    event.preventDefault();
                    suppressClickRows.add(row);
                    var targetChecked = !row.checked;
                    dragState = {
                        targetChecked: targetChecked,
                        sourceRow: row,
                        pointerId: event.pointerId ?? null,
                        visited: new Set(),
                        lastClientX: event.clientX,
                        lastClientY: event.clientY
                    };

                    if (row.setPointerCapture && event.pointerId !== undefined) {
                        try {
                            row.setPointerCapture(event.pointerId);
                        } catch (_) {
                            // Some browsers disallow capture on form controls.
                        }
                    }

                    form.classList.add("is-bulk-receipt-dragging");
                    applyDragSelection(dragState, row);
                });
            });

            form.addEventListener("pointermove", handleDragMove);

            document.addEventListener("pointermove", handleDragMove);
            document.addEventListener("pointerup", finishDragSelection);
            document.addEventListener("pointercancel", finishDragSelection);
            document.addEventListener("wheel", function () {
                if (!dragState) return;

                window.setTimeout(function () {
                    if (!dragState) return;
                    applyDragSelection(
                        dragState,
                        findBulkReceiptRowFromPoint(dragState.lastClientX, dragState.lastClientY)
                    );
                }, 0);
            }, { passive: true });

            if (selectAll) {
                selectAll.addEventListener("click", function () {
                    rows.forEach(function (row) { row.checked = true; });
                    syncSummary(true);
                });
            }

            if (clearAll) {
                clearAll.addEventListener("click", function () {
                    rows.forEach(function (row) { row.checked = false; });
                    syncSummary(true);
                });
            }

            if (toggle && panel) {
                toggle.addEventListener("click", function () {
                    setBulkReceiptPanelOpen(panel.hidden);
                });

                setBulkReceiptPanelOpen(form.getAttribute("data-bulk-receipt-collapsed") !== "true" && !panel.hidden);
            }

            if (terminalSelect) {
                terminalSelect.addEventListener("change", syncStorageTankOptions);
            }

            syncStorageTankOptions();
            syncSummary(false);
        });
    }

    // ---------------------------------------------------------------------
    // Double-submit guard
    // Locks a form's submit button(s) after the first valid submit so a slow
    // save (a few seconds online) can't be duplicated by an impatient second
    // click. Front-end half of the protection; the server idempotency token is
    // the authoritative backstop.
    //
    // Safe by design:
    //  - Opt out per-form or per-button with `data-no-submit-guard`.
    //  - Never touches cancel/back/delete buttons (only submit-type controls),
    //    and leaves modal-dismiss buttons ([data-bs-dismiss]) alone.
    //  - Respects native HTML5 validation: an invalid form is not locked, so the
    //    user can fix errors and resubmit.
    //  - Disables buttons on the next tick so the clicked button's name/value is
    //    still serialized into the POST body.
    //  - Re-enables on back/forward (bfcache) restore.
    // ---------------------------------------------------------------------
    // ناوبری SPA صفت‌های data-* بدنه را با صفحهٔ تازه هم‌تراز می‌کند و پرچمِ
    // روی body پاک می‌شود؛ پس پرچم باید در خودِ ماژول بماند، وگرنه هر ناوبری یک
    // شنوندهٔ submit تازه اضافه می‌کند و شنوندهٔ دوم، ارسال فرم را بلاک می‌کند.
    var submitGuardReady = false;

    function initializeSubmitGuard() {
        if (submitGuardReady) return;
        submitGuardReady = true;

        document.addEventListener("submit", function (event) {
            var form = event.target;
            if (!form || form.tagName !== "FORM" || event.defaultPrevented) return;
            if (form.hasAttribute("data-no-submit-guard")) return;

            // Second submit while the first is still in flight → block it.
            if (form.dataset.ptgSubmitting === "true") {
                event.preventDefault();
                event.stopImmediatePropagation();
                return;
            }

            // Respect native validation: if the form is invalid the browser will
            // not submit, so don't lock anything (user must be able to correct it).
            if (!form.noValidate && typeof form.checkValidity === "function" && !form.checkValidity()) {
                return;
            }

            form.dataset.ptgSubmitting = "true";

            var buttons = Array.prototype.slice.call(form.querySelectorAll(
                "button[type=submit], input[type=submit], input[type=image], button:not([type])"
            ));

            window.setTimeout(function () {
                buttons.forEach(function (btn) {
                    if (btn.disabled || btn.hasAttribute("data-no-submit-guard") || btn.hasAttribute("data-bs-dismiss")) {
                        return;
                    }

                    btn.dataset.ptgGuarded = "true";
                    var busyText = btn.getAttribute("data-submitting-text")
                        || (document.documentElement.lang === "en" ? "Saving…" : "در حال ثبت…");

                    if (btn.tagName === "BUTTON") {
                        btn.dataset.ptgOriginalHtml = btn.innerHTML;
                        btn.innerHTML = busyText;
                    } else {
                        btn.dataset.ptgOriginalValue = btn.value;
                        btn.value = busyText;
                    }

                    btn.disabled = true;
                    btn.classList.add("is-submitting");
                    btn.setAttribute("aria-busy", "true");
                });
            }, 0);
        }, false);

        // A field failing constraint validation means the submit was blocked →
        // release the guard so the corrected form can be resubmitted.
        document.addEventListener("invalid", function (event) {
            var field = event.target;
            if (field && field.form) resetSubmitGuard(field.form);
        }, true);

        // Restore buttons if the page is served from the back/forward cache.
        window.addEventListener("pageshow", function (event) {
            if (!event.persisted) return;
            document.querySelectorAll("form[data-ptg-submitting='true']").forEach(resetSubmitGuard);
        });
    }

    function resetSubmitGuard(form) {
        if (!form) return;
        form.dataset.ptgSubmitting = "false";
        form.querySelectorAll("[data-ptg-guarded='true']").forEach(function (btn) {
            btn.disabled = false;
            btn.classList.remove("is-submitting");
            btn.removeAttribute("aria-busy");
            if (btn.dataset.ptgOriginalHtml !== undefined) {
                btn.innerHTML = btn.dataset.ptgOriginalHtml;
                delete btn.dataset.ptgOriginalHtml;
            }
            if (btn.dataset.ptgOriginalValue !== undefined) {
                btn.value = btn.dataset.ptgOriginalValue;
                delete btn.dataset.ptgOriginalValue;
            }
            delete btn.dataset.ptgGuarded;
        });
    }

    // Expose re-initialization function for SPA navigation
    window.__ptgReinit = initializeShell;

    // Expose global functions needed by other modules
    window.PTG = window.PTG || {};
    window.PTG.initializeShell = initializeShell;
    window.PTG.dismissFlashAlert = dismissFlashAlert;
    window.PTG.openPageModal = openPageModal;
    window.PTG.closePageModal = closePageModal;

})();
