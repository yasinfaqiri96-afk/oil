/*
 * PTG Oil System - Core Module
 * Extracted from site.js for better organization
 */

(function () {
    "use strict";

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
        initializeQuickCreateForms();
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

    // هر بار باز شدن page-modal یک شمارهٔ تازه می‌گیرد تا تلاش دوبارهٔ بستن،
    // نمایشِ بعدی را به‌اشتباه نبندد.
    var pageModalOpenToken = 0;

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
                if (frame) {
                    frame._ptgQuickCreateSelect = null;
                    frame.setAttribute("src", "about:blank");
                }
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
        var quickCreateSelect = options && options.quickCreateSelect;
        if (quickCreateSelect) {
            modalUrl.searchParams.set("quickCreate", "1");
        }

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
            frame._ptgQuickCreateSelect = quickCreateSelect || null;

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

        pageModalOpenToken += 1;
        window.bootstrap.Modal.getOrCreateInstance(modalElement).show();
    }

    function closePageModal(options) {
        var modalElement = document.getElementById("ptgPageModal");
        var redirectUrl = options && options.redirectUrl ? options.redirectUrl : null;

        if (modalElement && window.bootstrap) {
            var instance = window.bootstrap.Modal.getOrCreateInstance(modalElement);

            // Bootstrap ignores hide() while the show transition is still running,
            // so a quick-create that resolves before shown.bs.modal would leave the
            // modal open forever. Arm a retry for that case; the open token keeps a
            // leftover retry from closing a later, unrelated open.
            var token = pageModalOpenToken;
            modalElement.addEventListener("shown.bs.modal", function () {
                if (pageModalOpenToken === token) instance.hide();
            }, { once: true });

            instance.hide();
        }

        if (redirectUrl) {
            if (document.querySelector("[data-contract-journey-page]")
                && window.PTG
                && typeof window.PTG.reloadContractJourneyTab === "function") {
                window.PTG.reloadContractJourneyTab(redirectUrl);
            } else {
                window.location.href = redirectUrl;
            }
        }
    }

    function completeQuickCreate(item) {
        var modalElement = document.getElementById("ptgPageModal");
        var frame = modalElement && modalElement.querySelector("[data-page-modal-frame]");
        var select = frame && frame._ptgQuickCreateSelect;
        if (!select || !select.isConnected || !item) return false;

        var valueField = select.dataset.akQuickCreateValueField || "id";
        var rawValue = item[valueField];
        var value = rawValue === null || rawValue === undefined ? "" : String(rawValue);
        var label = item.label === null || item.label === undefined ? "" : String(item.label).trim();
        if (!value || !label) return false;

        var option = Array.prototype.slice.call(select.options).find(function (candidate) {
            return candidate.value === value;
        });
        if (!option) {
            option = new Option(label, value);
            select.add(option);
        } else {
            option.textContent = label;
        }

        select.value = value;
        option.selected = true;
        select.dispatchEvent(new Event("input", { bubbles: true }));
        select.dispatchEvent(new Event("change", { bubbles: true }));
        closePageModal();
        return true;
    }

    function initializeQuickCreateForms() {
        if (window.parent === window) return;

        var currentUrl = new URL(window.location.href);
        if (currentUrl.searchParams.get("modal") !== "1"
            || currentUrl.searchParams.get("quickCreate") !== "1") {
            return;
        }

        document.querySelectorAll("form[method='post'], form[method='POST']").forEach(function (form) {
            if (form.dataset.ptgQuickCreateReady === "true") return;
            form.dataset.ptgQuickCreateReady = "true";

            form.addEventListener("submit", async function (event) {
                if (event.defaultPrevented) return;
                event.preventDefault();

                if (!lockSubmitGuard(form)) return;

                var action = new URL(form.action || window.location.href, window.location.origin);
                action.searchParams.set("modal", "1");
                action.searchParams.set("quickCreate", "1");

                try {
                    var formData;
                    try {
                        formData = new FormData(form, event.submitter);
                    } catch (error) {
                        formData = new FormData(form);
                        if (event.submitter && event.submitter.name) {
                            formData.append(event.submitter.name, event.submitter.value);
                        }
                    }

                    var response = await fetch(action.toString(), {
                        method: "POST",
                        body: formData,
                        credentials: "same-origin",
                        headers: {
                            "Accept": "application/json, text/html",
                            "X-Requested-With": "XMLHttpRequest"
                        }
                    });
                    var contentType = response.headers.get("content-type") || "";

                    if (contentType.indexOf("application/json") >= 0) {
                        var result = await response.json();
                        if (response.ok && result.success && result.item
                            && window.parent.PTG
                            && typeof window.parent.PTG.completeQuickCreate === "function"
                            && window.parent.PTG.completeQuickCreate(result.item)) {
                            return;
                        }

                        showQuickCreateError(form, result.message);
                        resetSubmitGuard(form);
                        return;
                    }

                    var html = await response.text();
                    document.open();
                    document.write(html);
                    document.close();
                } catch (error) {
                    showQuickCreateError(
                        form,
                        document.documentElement.lang === "en"
                            ? "Saving failed. Please try again."
                            : "ثبت اطلاعات ناموفق بود. دوباره تلاش کنید.");
                    resetSubmitGuard(form);
                }
            });
        });
    }

    function showQuickCreateError(form, message) {
        var summary = form.querySelector("[data-valmsg-summary='true'], .validation-summary-errors, .ak-form-alert");
        if (!summary) return;
        summary.classList.remove("validation-summary-valid");
        summary.classList.add("validation-summary-errors");
        summary.hidden = false;
        summary.innerHTML = "";
        var list = document.createElement("ul");
        var item = document.createElement("li");
        item.textContent = message || (document.documentElement.lang === "en"
            ? "The new item could not be selected."
            : "انتخاب خودکار رکورد جدید انجام نشد.");
        list.appendChild(item);
        summary.appendChild(list);
    }

    var bulkReceiptDelegationReady = false;
    var bulkReceiptDragState = null;
    var bulkReceiptSuppressedClicks = new WeakSet();

    function bulkReceiptRows(form) {
        return Array.prototype.slice.call(form.querySelectorAll("[data-bulk-receipt-row]"));
    }

    function syncBulkReceiptSummary(form, updateInput) {
        var checked = bulkReceiptRows(form).filter(function (row) { return row.checked; });
        var total = checked.reduce(function (sum, row) {
            return sum + (Number.parseFloat(row.getAttribute("data-bulk-receipt-qty") || "0") || 0);
        }, 0);
        var count = form.querySelector("[data-bulk-receipt-selected-count]");
        var quantity = form.querySelector("[data-bulk-receipt-selected-qty]");
        var input = form.querySelector("[data-bulk-receipt-total-input]");
        if (count) count.textContent = String(checked.length);
        if (quantity) quantity.textContent = new Intl.NumberFormat("en-US", {
            minimumFractionDigits: 4,
            maximumFractionDigits: 4
        }).format(total);
        if (updateInput && input) input.value = total.toFixed(4).replace(/\.?0+$/, "");
    }

    function syncStorageTankOptions() {
        var form = arguments[0];
        if (!form) return;
        var terminal = form.querySelector("[data-bulk-receipt-terminal-select]");
        var tank = form.querySelector("[data-bulk-receipt-tank-select]");
        if (!terminal || !tank) return;
        var terminalId = terminal.value || "";
        var keepSelection = false;
        Array.prototype.forEach.call(tank.options, function (option) {
            var visible = !option.value || (terminalId && option.getAttribute("data-terminal-id") === terminalId);
            option.hidden = !visible;
            option.disabled = !visible;
            if (visible && option.selected) keepSelection = true;
        });
        if (!keepSelection) tank.value = "";
    }

    function setBulkReceiptPanel(form, open) {
        var panel = form.querySelector("[data-bulk-receipt-panel]");
        var toggle = form.querySelector("[data-bulk-receipt-toggle]");
        if (!panel || !toggle) return;
        panel.hidden = !open;
        form.classList.toggle("is-bulk-receipt-open", open);
        toggle.setAttribute("aria-expanded", String(open));
        var icon = toggle.querySelector("i");
        if (icon) {
            icon.classList.toggle("bi-chevron-down", !open);
            icon.classList.toggle("bi-chevron-up", open);
        }
        var label = toggle.querySelector("[data-bulk-receipt-toggle-label]");
        if (label) label.textContent = open
            ? (label.getAttribute("data-close-label") || "Close form")
            : (label.getAttribute("data-open-label") || "Open form");
    }

    function showBulkReceiptError(form, message) {
        var alert = form.querySelector("[data-bulk-receipt-error]");
        if (!alert) {
            alert = document.createElement("div");
            alert.className = "alert alert-danger mb-3";
            alert.setAttribute("data-bulk-receipt-error", "true");
            form.prepend(alert);
        }
        alert.textContent = message || (document.documentElement.lang === "en"
            ? "Saving the receipt failed. Please try again."
            : "ثبت رسید ناموفق بود. اطلاعات را بررسی و دوباره تلاش کنید.");
        alert.hidden = false;
    }

    async function submitBulkReceipt(form, submitter) {
        if (!lockSubmitGuard(form)) return;
        var oldAlert = form.querySelector("[data-bulk-receipt-error]");
        if (oldAlert) oldAlert.hidden = true;
        try {
            var data;
            try { data = new FormData(form, submitter); }
            catch (_) { data = new FormData(form); }
            var response = await fetch(form.action, {
                method: "POST",
                body: data,
                credentials: "same-origin",
                headers: { "Accept": "application/json", "X-Requested-With": "XMLHttpRequest" }
            });
            var result = await response.json();
            if (!response.ok || !result.success) throw new Error(result.message || "Receipt save failed");
            if (window.PTG && typeof window.PTG.reloadContractJourneyTab === "function") {
                await window.PTG.reloadContractJourneyTab(result.redirectUrl || location.href);
                return;
            }
            location.assign(result.redirectUrl || location.href);
        } catch (error) {
            resetSubmitGuard(form);
            showBulkReceiptError(form, error && error.message);
        }
    }

    function initializeBulkReceiptForms() {
        document.querySelectorAll("[data-bulk-receipt-form]").forEach(function (form) {
            syncStorageTankOptions(form);
            syncBulkReceiptSummary(form, false);
            setBulkReceiptPanel(form, form.getAttribute("data-bulk-receipt-collapsed") !== "true");
        });
        if (bulkReceiptDelegationReady) return;
        bulkReceiptDelegationReady = true;

        document.addEventListener("change", function (event) {
            var form = event.target.closest && event.target.closest("[data-bulk-receipt-form]");
            if (!form) return;
            if (event.target.matches("[data-bulk-receipt-row]")) syncBulkReceiptSummary(form, true);
            if (event.target.matches("[data-bulk-receipt-terminal-select]")) syncStorageTankOptions(form);
        });
        document.addEventListener("click", function (event) {
            var form = event.target.closest && event.target.closest("[data-bulk-receipt-form]");
            if (!form) return;
            var row = event.target.closest("[data-bulk-receipt-row]");
            if (row && bulkReceiptSuppressedClicks.has(row)) {
                bulkReceiptSuppressedClicks.delete(row);
                event.preventDefault();
                return;
            }
            var action = event.target.closest("[data-bulk-receipt-select-all], [data-bulk-receipt-clear], [data-bulk-receipt-toggle]");
            if (!action) return;
            if (action.hasAttribute("data-bulk-receipt-toggle")) {
                var panel = form.querySelector("[data-bulk-receipt-panel]");
                setBulkReceiptPanel(form, !panel || panel.hidden);
                return;
            }
            var checked = action.hasAttribute("data-bulk-receipt-select-all");
            bulkReceiptRows(form).forEach(function (candidate) { candidate.checked = checked; });
            syncBulkReceiptSummary(form, true);
        });
        document.addEventListener("submit", function (event) {
            var form = event.target;
            if (!form || !form.matches("[data-bulk-receipt-form]")) return;
            event.preventDefault();
            event.stopImmediatePropagation();
            submitBulkReceipt(form, event.submitter);
        }, true);
        document.addEventListener("pointerdown", function (event) {
            var row = event.target.closest && event.target.closest("[data-bulk-receipt-row]");
            if (!row || (event.button !== undefined && event.button !== 0)) return;
            var form = row.closest("[data-bulk-receipt-form]");
            if (!form) return;
            event.preventDefault();
            bulkReceiptSuppressedClicks.add(row);
            bulkReceiptDragState = { form: form, checked: !row.checked, visited: new Set() };
            row.checked = bulkReceiptDragState.checked;
            bulkReceiptDragState.visited.add(row);
            form.classList.add("is-bulk-receipt-dragging");
            syncBulkReceiptSummary(form, true);
        });
        document.addEventListener("pointermove", function (event) {
            if (!bulkReceiptDragState) return;
            var element = document.elementFromPoint(event.clientX, event.clientY);
            var row = element && (element.closest("[data-bulk-receipt-row]") || (element.closest("tr") && element.closest("tr").querySelector("[data-bulk-receipt-row]")));
            if (!row || !bulkReceiptDragState.form.contains(row) || bulkReceiptDragState.visited.has(row)) return;
            bulkReceiptDragState.visited.add(row);
            row.checked = bulkReceiptDragState.checked;
            syncBulkReceiptSummary(bulkReceiptDragState.form, true);
        });
        ["pointerup", "pointercancel"].forEach(function (name) {
            document.addEventListener(name, function () {
                if (!bulkReceiptDragState) return;
                bulkReceiptDragState.form.classList.remove("is-bulk-receipt-dragging");
                bulkReceiptDragState = null;
            });
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

            if (!lockSubmitGuard(form)) {
                event.preventDefault();
                event.stopImmediatePropagation();
            }
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

    // تنها مالکِ «این فرم در حال ثبت است»: یک پرچم، یک بررسی اعتبار.
    // مسیرهای دیگر (مودال موجودیت، Quick Create) به‌جای پرچم و شرط خودشان همین را
    // صدا می‌زنند تا یک فرم دو منطق ضدتکرار نداشته باشد.
    // قفلِ کهنه: اگر ارسال قبلی هرگز به ناوبری نرسید (خطای شبکه، لغو مرورگر، پاسخ
    // ناقص) پرچم روی فرم می‌ماند و از آن به بعد هر ارسالِ بعدی بی‌صدا حذف می‌شد —
    // یعنی هیچ POST ساخته نمی‌شد و کاربر هیچ دلیلی نمی‌دید. قفل بعد از این مدت کهنه
    // شمرده و آزاد می‌شود تا صفحه هرگز به‌طور دائم مرده نماند.
    //
    // این مهلت باید از نگهبانِ ثبتِ صفحه‌ها کوتاه‌تر نباشد (فعلاً ۱۲۰ ثانیه در فرم ثبت
    // بارگیری). اگر کوتاه‌تر باشد، در فاصلهٔ بین این دو، قفل خودبه‌خود کهنه می‌شود در حالی
    // که POST هنوز در جریان است و ارسال دوم اجازهٔ عبور می‌گیرد؛ یعنی همان ثبت تکراری که
    // این گارد جلویش را می‌گرفت. صفحه‌ها با PTG.releaseFormSubmit زودتر قفل را پس می‌دهند.
    var STALE_SUBMIT_LOCK_MS = 120000;

    function isSubmitLockStale(form) {
        var startedAt = Number(form.dataset.ptgSubmittingAt || 0);
        return !startedAt || (Date.now() - startedAt) > STALE_SUBMIT_LOCK_MS;
    }

    function reportFirstInvalidField(form) {
        var invalid = form.querySelector(":invalid");
        if (typeof form.reportValidity === "function") form.reportValidity();
        if (!invalid) return;
        if (typeof invalid.scrollIntoView === "function") {
            invalid.scrollIntoView({ block: "center", behavior: "smooth" });
        }
        try { invalid.focus({ preventScroll: true }); } catch (_) { /* hidden control */ }
    }

    // ارسالِ بلوک‌شده باید همیشه دلیل داشته باشد؛ صفحه‌ها می‌توانند به این رویداد
    // گوش بدهند و پیام کاربرپسند نشان بدهند.
    function announceSubmitBlocked(form, reason) {
        try { console.warn("[PTG] form submit blocked:", reason, form); } catch (_) { }
        form.dispatchEvent(new CustomEvent("ptg:submit-blocked", {
            bubbles: true,
            detail: { reason: reason }
        }));
    }

    function claimFormSubmit(form) {
        if (!form) return false;
        if (form.dataset.ptgSubmitting === "true") {
            if (!isSubmitLockStale(form)) {
                announceSubmitBlocked(form, "in-progress");
                return false;
            }

            // قفل کهنه است: آزاد کن و اجازهٔ ارسال دوباره بده.
            resetSubmitGuard(form);
        }

        if (!form.noValidate && typeof form.checkValidity === "function" && !form.checkValidity()) {
            announceSubmitBlocked(form, "invalid");
            reportFirstInvalidField(form);
            return false;
        }

        form.dataset.ptgSubmitting = "true";
        form.dataset.ptgSubmittingAt = String(Date.now());
        return true;
    }

    function lockSubmitGuard(form) {
        if (!claimFormSubmit(form)) return false;

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
        return true;
    }

    function resetSubmitGuard(form) {
        if (!form) return;
        form.dataset.ptgSubmitting = "false";
        delete form.dataset.ptgSubmittingAt;
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
    window.PTG.completeQuickCreate = completeQuickCreate;
    // برای مسیرهایی که ظاهر «در حال ثبت» خودشان را دارند (modal-design-system):
    // فقط قفل و بررسی اعتبار مشترک، بدون دست‌زدن به دکمه‌ها.
    window.PTG.claimFormSubmit = claimFormSubmit;
    window.PTG.releaseFormSubmit = resetSubmitGuard;

    // ---------------------------------------------------------------------
    // Bootstrap — must stay at the very BOTTOM of the module.
    //
    // این فراخوانی قبلاً بالای فایل بود. چون صفحه‌ها core.js را با defer/انتهای
    // body بار می‌کنند، readyState دیگر "loading" نیست و initializeShell()
    // همان‌جا اجرا می‌شد — یعنی *پیش از* اجرای دستورهای `var ...Ready = false`
    // که پایین‌تر در همین ماژول‌اند. hoisting فقط اعلان را بالا می‌برد نه مقدار
    // را؛ پس پرچم در نخستین اجرا undefined بود، بایندینگ انجام می‌شد، پرچم true
    // می‌شد و بلافاصله بعد، خطِ `var submitGuardReady = false;` دوباره صفرش
    // می‌کرد. نتیجه: هر ناوبری SPA یک شنوندهٔ submit دوم اضافه می‌کرد؛ شنوندهٔ
    // اول قفل را می‌گرفت و شنوندهٔ دوم همان ارسال را با دلیل "in-progress" لغو
    // می‌کرد. هیچ POSTی ساخته نمی‌شد و فقط رفرش کامل صفحه درستش می‌کرد.
    // همین اشکال روی pageModalReady و bulkReceiptDelegationReady هم بود.
    // ---------------------------------------------------------------------
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

})();
