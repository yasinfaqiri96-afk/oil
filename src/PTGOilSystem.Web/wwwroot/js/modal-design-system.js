/*
 * PTG Oil System - Modal Design System
 * Lightweight UI behavior for enterprise modal-style forms.
 */

(function () {
    "use strict";

    function initializeModalDesignSystem(root) {
        var scope = root || document;
        scope.querySelectorAll("[data-receipt-create-form]").forEach(initializeReceiptCreateForm);
        initializeEntityModalTriggers();
        initializeEntityModalFormSubmit();
    }

    /*
     * Entity-modal AJAX submit.
     * Master-data / parties create modals post their form here instead of doing
     * a full SPA navigation. On a valid save the controller redirects to the
     * list, so we close the modal and refresh the list. On an invalid
     * ModelState the controller re-renders the same form (HTTP 200) — we swap
     * just the form body back into the open modal so the validation errors show
     * inside the modal and the full create page never opens.
     * No backend / validation / business-logic change is involved.
     */
    function initializeEntityModalFormSubmit() {
        window.PTG = window.PTG || {};
        if (window.PTG.entityModalFormSubmitReady === true) return;
        window.PTG.entityModalFormSubmitReady = true;

        document.addEventListener("submit", function (event) {
            var form = event.target;
            if (!form || form.tagName !== "FORM") return;
            if (!form.hasAttribute("data-ptg-entity-modal-form")) return;
            event.preventDefault();
            event.stopPropagation();
            submitEntityModalForm(form);
        }, true);
    }

    // قفل ضدتکرار و بررسی اعتبار از core.js می‌آید (پرچم مشترک data-ptg-submitting).
    // این فایل فقط ظاهر «در حال ثبت» دکمه‌های مودال را نگه می‌دارد.
    function claimSubmit(form) {
        if (window.PTG && typeof window.PTG.claimFormSubmit === "function") {
            return window.PTG.claimFormSubmit(form);
        }
        if (form.dataset.ptgSubmitting === "true") return false;
        form.dataset.ptgSubmitting = "true";
        return true;
    }

    function releaseSubmit(form) {
        if (window.PTG && typeof window.PTG.releaseFormSubmit === "function") {
            window.PTG.releaseFormSubmit(form);
            return;
        }
        form.dataset.ptgSubmitting = "false";
    }

    function submitEntityModalForm(form) {
        if (!claimSubmit(form)) return;

        var modal = form.closest(".modal, [data-entity-modal]");
        var buttons = collectModalSubmitButtons(form);
        setModalButtonsBusy(buttons, true);

        var action = form.getAttribute("action") || window.location.href;
        var method = (form.getAttribute("method") || "post").toUpperCase();

        fetch(action, {
            method: method,
            body: new FormData(form),
            credentials: "same-origin",
            redirect: "follow",
            headers: {
                "X-PTG-SPA": "1",
                "X-PTG-Modal": "1",
                "X-Requested-With": "XMLHttpRequest"
            }
        }).then(function (response) {
            return response.text().then(function (html) {
                return { html: html, url: response.url, redirected: response.redirected };
            });
        }).then(function (result) {
            // Valid save → controller redirected to the list.
            if (result.redirected) {
                handleEntityModalSuccess(modal, result.url, result.html);
                return;
            }
            var replaced = replaceEntityModalFormBody(form, result.html);
            if (replaced) {
                setModalButtonsBusy(buttons, false);
                releaseSubmit(form);
            } else {
                // No re-rendered form found → treat as a navigation.
                handleEntityModalSuccess(modal, result.url || action, result.html);
            }
        }).catch(function () {
            // Network/parse failure → fall back to a native submit so the user
            // keeps the normal (pre-fix) behavior and nothing is lost.
            setModalButtonsBusy(buttons, false);
            releaseSubmit(form);
            form.removeAttribute("data-ptg-entity-modal-form");
            form.submit();
        });
    }

    function replaceEntityModalFormBody(form, html) {
        var scroll = form.querySelector(".ptg-modal-form-scroll");
        if (!scroll) return false;

        var doc = new DOMParser().parseFromString(html, "text/html");
        var newScroll = doc.querySelector(".ptg-modal-form-scroll");
        if (!newScroll) return false;

        // The full create-page shell embeds its own action bar inside the scroll
        // area; the modal keeps its footer buttons, so drop the page version.
        newScroll.querySelectorAll(".form-actions").forEach(function (node) {
            node.remove();
        });

        scroll.innerHTML = newScroll.innerHTML;
        scroll.scrollTop = 0;

        reparseModalUnobtrusive(form);
        focusFirstModalError(form);
        return true;
    }

    function reparseModalUnobtrusive(form) {
        var jq = window.jQuery || window.$;
        if (jq && jq.validator && jq.validator.unobtrusive) {
            try {
                jq.validator.unobtrusive.parse(form);
            } catch (_) {}
        }
    }

    function focusFirstModalError(form) {
        var field = form.querySelector(
            ".input-validation-error, input.input-validation-error, select.input-validation-error, textarea.input-validation-error"
        );
        if (!field || typeof field.focus !== "function") return;
        try { field.focus({ preventScroll: true }); } catch (_) { field.focus(); }
    }

    function handleEntityModalSuccess(modal, url, html) {
        // Preferred path: render the already-fetched redirect HTML in place so
        // the read-once TempData flash survives (swap() also disposes the modal
        // and its backdrop). Falls back to a fresh navigation otherwise.
        if (html && window.PTG && typeof window.PTG.spaApplyHtml === "function") {
            window.PTG.spaApplyHtml(url, html);
            return;
        }
        if (modal && window.bootstrap && window.bootstrap.Modal) {
            try { window.bootstrap.Modal.getOrCreateInstance(modal).hide(); } catch (_) {}
        }
        if (window.PTG && typeof window.PTG.spaNavigate === "function") {
            window.PTG.spaNavigate(url);
        } else {
            window.location.assign(url);
        }
    }

    function collectModalSubmitButtons(form) {
        var buttons = Array.prototype.slice.call(
            form.querySelectorAll("button[type='submit'], input[type='submit']")
        );
        var id = form.getAttribute("id");
        if (id) {
            var escaped = (window.CSS && CSS.escape) ? CSS.escape(id) : id.replace(/["\\]/g, "\\$&");
            document.querySelectorAll("[form='" + escaped + "']").forEach(function (el) {
                if ((el.tagName === "BUTTON" || el.tagName === "INPUT") && buttons.indexOf(el) === -1) {
                    buttons.push(el);
                }
            });
        }
        return buttons;
    }

    function setModalButtonsBusy(buttons, busy) {
        buttons.forEach(function (button) {
            button.disabled = busy;
            button.classList.toggle("is-busy", busy);
        });
    }

    function initializeEntityModalTriggers() {
        window.PTG = window.PTG || {};
        if (window.PTG.entityModalTriggersReady === true) return;

        function resetModalScroll(modal) {
            if (!modal) return;
            modal.querySelectorAll(".ptg-modal-form-scroll, .modal-body, .ptg-reference-main-panel").forEach(function (node) {
                node.scrollTop = 0;
            });
        }

        function syncModalDensity(modal) {
            if (!modal || !modal.classList || !modal.classList.contains("ptg-reference-modal")) return;

            var fields = modal.querySelectorAll(
                "input:not([type='hidden']):not([type='file']):not([type='checkbox']):not([type='radio']), select, textarea"
            );
            var count = fields ? fields.length : 0;

            modal.classList.toggle("ptg-modal-field-heavy", count > 8);
            modal.classList.toggle("ptg-modal-field-dense", count > 12);
        }

        document.addEventListener("click", function (event) {
            var opener = event.target.closest("[data-entity-modal-open]");
            if (opener) {
                var targetId = (opener.getAttribute("data-entity-modal-open") || "").replace(/^#/, "");
                var modal = targetId ? document.getElementById(targetId) : null;
                if (modal && window.bootstrap && window.bootstrap.Modal) {
                    event.preventDefault();
                    syncModalDensity(modal);
                    resetModalScroll(modal);
                    window.bootstrap.Modal.getOrCreateInstance(modal).show();
                }
                return;
            }

            var closer = event.target.closest("[data-entity-modal-close]");
            if (!closer || closer.hasAttribute("data-bs-dismiss")) return;

            var activeModal = closer.closest("[data-entity-modal], .modal");
            if (activeModal && window.bootstrap && window.bootstrap.Modal) {
                event.preventDefault();
                window.bootstrap.Modal.getOrCreateInstance(activeModal).hide();
            }
        });

        document.addEventListener("show.bs.modal", function (event) {
            syncModalDensity(event.target);
            resetModalScroll(event.target);
        });

        document.addEventListener("shown.bs.modal", function (event) {
            syncModalDensity(event.target);
            resetModalScroll(event.target);
        });

        window.PTG.entityModalTriggersReady = true;
    }

    function initializeReceiptCreateForm(form) {
        if (!form || form.dataset.receiptCreateReady === "true") return;

        // ---- helpers -------------------------------------------------------
        function readDecimal(input) {
            if (!input || !input.value) return null;
            var parsed = Number.parseFloat(input.value.toString().replace(/,/g, ""));
            return Number.isFinite(parsed) ? parsed : null;
        }

        function formatQuantity(value) {
            if (value === null || value === undefined) return "";
            return value.toLocaleString(undefined, { maximumFractionDigits: 4 }) + " MT";
        }

        function setPreviewValue(key, value) {
            form.querySelectorAll('[data-preview-value="' + key + '"]').forEach(function (node) {
                node.textContent = value && String(value).trim() ? value : "—";
            });
        }

        function selectedText(id) {
            var element = document.getElementById(id);
            if (!element) return "";
            if (element.tagName === "SELECT") {
                var option = element.options[element.selectedIndex];
                return option && option.value ? option.text : "";
            }
            return element.value || "";
        }

        function splitTokens(value) {
            return String(value || "").split(/[\s,]+/).filter(Boolean);
        }

        // ---- shortage (loss) ----------------------------------------------
        var lossEnabledInput = form.querySelector("[data-loss-enabled-value]");
        var lossPanel = form.querySelector("[data-loss-panel]");
        var lossButtons = form.querySelectorAll("[data-loss-pick]");
        var lossQuantityInput = form.querySelector("[data-loss-auto-quantity]");
        var receivedInput = document.getElementById("ReceivedQuantityMt");
        var shortageHint = form.querySelector("[data-receipt-shortage-hint]");
        var baseQuantity = Number.parseFloat((receivedInput && receivedInput.dataset.receiptBaseQuantity) || "0") || 0;

        function lossIsEnabled() {
            return !!lossEnabledInput && String(lossEnabledInput.value) === "true";
        }

        function syncLossPanel() {
            var enabled = lossIsEnabled();

            lossButtons.forEach(function (button) {
                var selected = (button.getAttribute("data-loss-pick") === "yes") === enabled;
                button.classList.toggle("is-selected", selected);
                button.setAttribute("aria-pressed", selected ? "true" : "false");
            });

            if (!lossPanel) return;
            lossPanel.classList.toggle("d-none", !enabled);
            lossPanel.querySelectorAll("input, select, textarea").forEach(function (field) {
                if (field.type !== "hidden") field.disabled = !enabled;
            });
        }

        lossButtons.forEach(function (button) {
            button.addEventListener("click", function () {
                if (!lossEnabledInput) return;
                lossEnabledInput.value = button.getAttribute("data-loss-pick") === "yes" ? "true" : "false";
                syncLossPanel();
                syncReceiptPreview();
            });
        });

        // کسری = باقیماندهٔ بارگیری − مقدار رسید. تا وقتی کاربر فیلد کسری را دستی
        // تغییر نداده، همین مقدار در آن نوشته می‌شود.
        if (lossQuantityInput) {
            if (lossQuantityInput.value && lossQuantityInput.value.toString().trim()) {
                lossQuantityInput.dataset.lossQuantityTouched = "true";
            }
            lossQuantityInput.addEventListener("input", function () {
                lossQuantityInput.dataset.lossQuantityTouched = "true";
                syncReceiptPreview();
            });
        }

        function computeShortage() {
            var received = readDecimal(receivedInput);
            if (received === null || baseQuantity <= 0) return null;
            return Math.round(Math.max(baseQuantity - received, 0) * 10000) / 10000;
        }

        function syncAutoLoss() {
            var shortage = computeShortage();

            if (shortageHint) {
                var template = shortageHint.getAttribute("data-hint-template") || "{0}";
                shortageHint.hidden = shortage === null;
                if (shortage !== null) {
                    shortageHint.textContent = template.replace(
                        "{0}",
                        shortage.toLocaleString(undefined, { maximumFractionDigits: 4 }));
                }
            }

            if (!lossQuantityInput || lossQuantityInput.dataset.lossQuantityTouched === "true") return;
            lossQuantityInput.value = shortage === null || shortage <= 0 ? "" : String(shortage);
        }

        // ---- destination scenarios ----------------------------------------
        var scenarioMap = {
            inventory: {
                receipt: form.dataset.receiptToInventory,
                allocation: form.dataset.allocToInventory
            },
            truck: {
                receipt: form.dataset.receiptDirectDispatch,
                allocation: form.dataset.allocDirectTruck
            },
            sale: {
                receipt: form.dataset.receiptDirectDispatch,
                allocation: form.dataset.allocDirectSale
            },
            transfer: {
                receipt: form.dataset.receiptDirectDispatch,
                allocation: form.dataset.allocTransfer
            }
        };

        var receiptDestinationInput = document.getElementById("ReceiptDestination");
        var allocationDestinationInput = document.getElementById("AllocationDestination");
        var scenarioKeyInput = document.getElementById("ScenarioKey");
        var scenarioButtons = form.querySelectorAll("[data-scenario-pick]");
        var scenarioPanels = form.querySelectorAll("[data-scenario-panel]");
        var copiedValueFields = form.querySelectorAll("[data-copy-value-to]");
        var activeScenario = "inventory";

        function syncCopiedValueFields() {
            copiedValueFields.forEach(function (source) {
                var targetId = source.getAttribute("data-copy-value-to");
                if (!targetId) return;
                var target = form.querySelector("#" + targetId);
                if (!target) return;
                target.value = source.value || "";
            });
        }

        function scenarioPanelMatches(panel, scenario) {
            return splitTokens(panel.getAttribute("data-scenario-panel")).indexOf(scenario) !== -1;
        }

        function syncScenarioPanelFields(panel, isActive) {
            panel.querySelectorAll("input, select, textarea").forEach(function (field) {
                if (field.type === "hidden") return;
                field.disabled = !isActive;
            });
        }

        function applyScenario(scenario) {
            if (!scenarioMap[scenario]) scenario = "inventory";
            activeScenario = scenario;
            var config = scenarioMap[scenario];

            if (receiptDestinationInput) receiptDestinationInput.value = config.receipt || "";
            if (allocationDestinationInput) allocationDestinationInput.value = config.allocation || "";
            if (scenarioKeyInput) scenarioKeyInput.value = scenario;

            scenarioButtons.forEach(function (button) {
                var selected = button.getAttribute("data-scenario-pick") === scenario;
                button.classList.toggle("is-selected", selected);
                button.setAttribute("aria-pressed", selected ? "true" : "false");
            });

            scenarioPanels.forEach(function (panel) {
                var isActive = scenarioPanelMatches(panel, scenario);
                panel.classList.toggle("d-none", !isActive);
                syncScenarioPanelFields(panel, isActive);
            });

            syncCopiedValueFields();
            syncReceiptPreview();
        }

        scenarioButtons.forEach(function (button) {
            button.addEventListener("click", function () {
                applyScenario(button.getAttribute("data-scenario-pick"));
            });
        });

        function activeScenarioButton() {
            return form.querySelector('[data-scenario-pick="' + activeScenario + '"]');
        }

        function destinationDetailText() {
            if (activeScenario === "inventory") return selectedText("StorageTankId");
            if (activeScenario === "sale") return selectedText("SaleCustomerId");
            if (activeScenario === "truck") {
                return [selectedText("DirectTruckId"), selectedText("DestinationLocationId")]
                    .filter(Boolean)
                    .join(" — ");
            }
            return [selectedText("DestinationTerminalId"), selectedText("DestinationLocationId")]
                .filter(Boolean)
                .join(" — ");
        }

        // ---- live summary (step 5) ----------------------------------------
        function syncReceiptPreview() {
            syncAutoLoss();

            var received = readDecimal(receivedInput);
            var loss = lossIsEnabled() ? readDecimal(lossQuantityInput) : null;
            var button = activeScenarioButton();

            setPreviewValue("receivedQuantity", received === null ? "" : formatQuantity(received));
            setPreviewValue("destinationType", button ? button.getAttribute("data-scenario-label") : "");
            setPreviewValue("inventoryEffect", button ? button.getAttribute("data-scenario-effect") : "");
            setPreviewValue("destinationDetail", destinationDetailText());
            setPreviewValue("lossQuantity", loss ? formatQuantity(loss) : "");
            setPreviewValue(
                "consumedQuantity",
                received === null ? "" : formatQuantity(Math.round((received + (loss || 0)) * 10000) / 10000));
            setPreviewValue(
                "inventoryDelta",
                activeScenario === "inventory" && received !== null ? "+ " + formatQuantity(received) : "");
            setPreviewValue("reference", selectedText("ReferenceDocument"));
            setPreviewValue("terminal", selectedText("TerminalId"));
            setPreviewValue("storageTank", selectedText("StorageTankId"));
            setPreviewValue("customer", selectedText("SaleCustomerId"));
            setPreviewValue("truck", selectedText("DirectTruckId"));
            setPreviewValue("driver", selectedText("DirectDriverId"));
        }

        [
            "ReferenceDocument",
            "ReceivedQuantityMt",
            "TerminalId",
            "StorageTankId",
            "DestinationTerminalId",
            "DestinationStorageTankId",
            "DestinationLocationId",
            "SaleCustomerId",
            "DirectTruckId",
            "DirectDriverId"
        ].forEach(function (id) {
            var element = document.getElementById(id);
            if (!element) return;
            element.addEventListener("input", syncReceiptPreview);
            element.addEventListener("change", syncReceiptPreview);
        });

        copiedValueFields.forEach(function (field) {
            field.addEventListener("input", syncCopiedValueFields);
            field.addEventListener("change", syncCopiedValueFields);
        });

        syncLossPanel();
        applyScenario(scenarioKeyInput ? scenarioKeyInput.value : "inventory");
        form.dataset.receiptCreateReady = "true";
    }

    window.initializeModalDesignSystem = initializeModalDesignSystem;
    window.initializeReceiptCreateForm = initializeReceiptCreateForm;
    window.PTG = window.PTG || {};
    window.PTG.initializeModalDesignSystem = initializeModalDesignSystem;

    function bootModalDesignSystem() {
        initializeModalDesignSystem(document);
    }

    if (window.PTG.modalDesignSystemReady === true) {
        bootModalDesignSystem();
    } else {
        window.PTG.modalDesignSystemReady = true;
        if (document.readyState === "loading") {
            document.addEventListener("DOMContentLoaded", bootModalDesignSystem, { once: true });
        } else {
            bootModalDesignSystem();
        }

        window.addEventListener("ptg:page-ready", bootModalDesignSystem);
    }
})();
