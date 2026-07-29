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

        var toggle = document.getElementById("lossEnabledToggle");
        var panel = document.getElementById("lossPanelFields");
        var hint = document.getElementById("lossPanelHint");

        function syncLossPanel() {
            if (!toggle || !panel) return;
            var enabled = toggle.checked;
            var openByDefault = panel.dataset.lossOpenDefault === "true" && toggle.dataset.lossTouched !== "true";
            var visible = enabled || openByDefault;
            panel.style.display = visible ? "" : "none";
            panel.querySelectorAll("input, select, textarea").forEach(function (field) {
                if (field.type !== "hidden") field.disabled = !visible;
            });
            if (hint) hint.style.display = enabled ? "none" : "";
        }

        if (toggle) {
            toggle.addEventListener("change", function () {
                toggle.dataset.lossTouched = "true";
                syncLossPanel();
            });
            if (panel) {
                panel.querySelectorAll("input, select, textarea").forEach(function (field) {
                    field.addEventListener("input", function () {
                        if (!toggle.checked && field.value && field.value.toString().trim()) {
                            toggle.checked = true;
                            toggle.dataset.lossTouched = "true";
                            syncLossPanel();
                        }
                    });
                });
            }
            syncLossPanel();
        }

        // Loss capture mode: immediate (known now) vs deferred tank settlement.
        var lossModeValue = form.querySelector("[data-loss-mode-value]");
        var lossModeButtons = form.querySelectorAll("[data-loss-mode-pick]");
        var lossImmediateBlock = form.querySelector("[data-loss-immediate-block]");
        var lossDeferredNote = form.querySelector("[data-loss-deferred-note]");
        var LOSS_MODE_DEFERRED = "2";

        function syncLossMode() {
            if (!lossModeValue) return;
            var currentValue = String(lossModeValue.value || "1");
            var isDeferred = currentValue === LOSS_MODE_DEFERRED;

            lossModeButtons.forEach(function (btn) {
                var selected = btn.getAttribute("data-loss-mode-set") === currentValue;
                btn.classList.toggle("is-selected", selected);
                btn.setAttribute("aria-pressed", selected ? "true" : "false");
            });

            if (isDeferred && toggle) {
                toggle.checked = false;
                toggle.dataset.lossTouched = "true";
                toggle.disabled = true;
            } else if (toggle) {
                toggle.disabled = false;
            }

            if (lossImmediateBlock) {
                lossImmediateBlock.style.display = isDeferred ? "none" : "";
            }

            if (lossDeferredNote) {
                lossDeferredNote.classList.toggle("d-none", !isDeferred);
            }

            if (isDeferred) {
                if (lossImmediateBlock) {
                    lossImmediateBlock.querySelectorAll("input, select, textarea").forEach(function (field) {
                        if (field.type !== "hidden") field.disabled = true;
                    });
                }
            } else {
                syncLossPanel();
            }
        }

        lossModeButtons.forEach(function (btn) {
            btn.addEventListener("click", function () {
                if (!lossModeValue) return;
                lossModeValue.value = btn.getAttribute("data-loss-mode-set") || "1";
                syncLossMode();
            });
        });
        syncLossMode();

        var receiptDestinationInput = document.getElementById("ReceiptDestination");
        var allocationDestinationInput = document.getElementById("AllocationDestination");
        var scenarioKeyInput = document.getElementById("ScenarioKey");
        var scenarioButtons = form.querySelectorAll("[data-scenario-pick]");
        var scenarioPanels = form.querySelectorAll("[data-scenario-panel]");
        var scenarioBanners = form.querySelectorAll("[data-scenario-banner]");
        var scenarioSubtitles = form.querySelectorAll("[data-scenario-subtitle]");
        var scenarioFinals = form.querySelectorAll("[data-scenario-final]");
        var scenarioLabels = form.querySelectorAll("[data-scenario-label-for]");
        var copiedValueFields = form.querySelectorAll("[data-copy-value-to]");

        var scenarioMap = {
            inventory: {
                receipt: form.dataset.receiptToInventory,
                allocation: form.dataset.allocToInventory,
                label: "ورود به موجودی",
                effect: "موجودی زیاد می‌شود"
            },
            truck: {
                receipt: form.dataset.receiptDirectDispatch,
                allocation: form.dataset.allocDirectTruck,
                label: "دیسپچ مستقیم",
                effect: "فقط ردیابی و دیسپچ ثبت می‌شود"
            },
            sale: {
                receipt: form.dataset.receiptDirectDispatch,
                allocation: form.dataset.allocDirectSale,
                label: "فروش مستقیم",
                effect: "موجودی جعلی ساخته نمی‌شود"
            },
            transfer: {
                receipt: form.dataset.receiptDirectDispatch,
                allocation: form.dataset.allocTransfer,
                label: "انتقال به ترمینال دیگر",
                effect: "در مسیر است"
            },
            mixed: {
                receipt: form.dataset.receiptMixed,
                allocation: form.dataset.allocToInventory,
                label: "مختلط",
                effect: "بر اساس خطوط تخصیص اثر می‌گذارد"
            }
        };

        function splitTokens(value) {
            return String(value || "").split(/[\s,]+/).filter(Boolean);
        }

        function syncMixedAllocationRow(row) {
            var destinationSelect = row.querySelector("[data-mixed-destination-select]");
            var destination = destinationSelect ? String(destinationSelect.value || "") : "";
            row.querySelectorAll("[data-mixed-destination-field]").forEach(function (cell) {
                var visible = splitTokens(cell.getAttribute("data-mixed-destination-field")).indexOf(destination) !== -1;
                cell.hidden = !visible;
                cell.classList.toggle("d-none", !visible);
                cell.querySelectorAll("input, select, textarea").forEach(function (field) {
                    if (field.type !== "hidden") {
                        field.disabled = !visible;
                    }
                });
            });
        }

        function syncMixedAllocationRows() {
            form.querySelectorAll("[data-mixed-allocation-row]").forEach(syncMixedAllocationRow);
        }

        form.querySelectorAll("[data-mixed-destination-select]").forEach(function (select) {
            select.addEventListener("change", function () {
                var row = select.closest("[data-mixed-allocation-row]");
                if (row) syncMixedAllocationRow(row);
            });
        });

        function setPreviewValue(key, value) {
            document.querySelectorAll('[data-preview-value="' + key + '"]').forEach(function (node) {
                node.textContent = value && String(value).trim() ? value : "-";
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

        function syncCopiedValueFields() {
            copiedValueFields.forEach(function (source) {
                var targetId = source.getAttribute("data-copy-value-to");
                if (!targetId) return;
                var target = form.querySelector("#" + targetId);
                if (!target) return;
                target.value = source.value || "";
                target.dispatchEvent(new Event("change", { bubbles: true }));
            });
        }

        function scenarioPanelMatches(panel, scenario) {
            var keys = (panel.getAttribute("data-scenario-panel") || "")
                .split(/\s+/)
                .filter(Boolean);
            return keys.indexOf(scenario) !== -1;
        }

        function syncScenarioPanelFields(panel, isActive) {
            panel.querySelectorAll("input, select, textarea").forEach(function (field) {
                if (field.type === "hidden") return;
                field.disabled = !isActive;
            });
        }

        function applyScenario(scenario) {
            if (!scenarioMap[scenario]) scenario = "inventory";
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

            scenarioBanners.forEach(function (banner) {
                banner.classList.toggle("d-none", banner.getAttribute("data-scenario-banner") !== scenario);
            });

            scenarioSubtitles.forEach(function (node) {
                node.classList.toggle("d-none", node.getAttribute("data-scenario-subtitle") !== scenario);
            });

            scenarioFinals.forEach(function (node) {
                node.classList.toggle("d-none", node.getAttribute("data-scenario-final") !== scenario);
            });

            scenarioLabels.forEach(function (label) {
                var customText = label.getAttribute("data-label-" + scenario);
                var fallbackText = label.getAttribute("data-label-default");
                label.textContent = customText || fallbackText || label.textContent;
            });

            setPreviewValue("destinationType", config.label);
            setPreviewValue("inventoryEffect", config.effect);
            syncCopiedValueFields();
            syncMixedAllocationRows();
            syncReceiptPreview();
        }

        scenarioButtons.forEach(function (button) {
            button.addEventListener("click", function () {
                applyScenario(button.getAttribute("data-scenario-pick"));
            });
        });

        var differencePreview = form.querySelector("[data-receipt-difference-preview]");
        var differenceValue = form.querySelector("[data-receipt-difference-value]");
        var receivedInput = document.getElementById("ReceivedQuantityMt");
        var actualInput = document.getElementById("ActualArrivedQuantityMt");

        function readDecimal(input) {
            if (!input || !input.value) return null;
            var parsed = Number.parseFloat(input.value.toString().replace(/,/g, ""));
            return Number.isFinite(parsed) ? parsed : null;
        }

        function formatQuantity(input) {
            var parsed = readDecimal(input);
            return parsed === null ? "" : parsed.toLocaleString(undefined, { maximumFractionDigits: 4 }) + " MT";
        }

        function syncDifferencePreview() {
            if (!differencePreview || !differenceValue) return;
            var loaded = Number.parseFloat(differencePreview.dataset.loadedQuantity || "0") || 0;
            var actual = readDecimal(actualInput);
            var received = readDecimal(receivedInput);
            var compareQuantity = actual ?? received;

            if (!compareQuantity || loaded <= 0) {
                differenceValue.textContent = "-";
                setPreviewValue("quantityDifference", "-");
                return;
            }

            var difference = (loaded - compareQuantity).toFixed(4);
            differenceValue.textContent = difference;
            setPreviewValue("quantityDifference", difference + " MT");
        }

        function syncReceiptPreview() {
            setPreviewValue("reference", selectedText("ReferenceDocument"));
            setPreviewValue("receivedQuantity", formatQuantity(receivedInput));
            setPreviewValue("terminal", selectedText("TerminalId"));
            setPreviewValue("storageTank", selectedText("StorageTankId") || selectedText("DestinationStorageTankId"));
            setPreviewValue("customer", selectedText("SaleCustomerId"));
            setPreviewValue("truck", selectedText("DirectTruckPlateNumber") || selectedText("DirectTruckId"));
            setPreviewValue("driver", selectedText("DirectDriverName") || selectedText("DirectDriverId"));
            syncDifferencePreview();
        }

        [
            "ReferenceDocument",
            "ReceivedQuantityMt",
            "ActualArrivedQuantityMt",
            "TerminalId",
            "StorageTankId",
            "DestinationStorageTankId",
            "SaleCustomerId",
            "DirectTruckPlateNumber",
            "DirectTruckId",
            "DirectDriverName",
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

        applyScenario(scenarioKeyInput ? scenarioKeyInput.value : "inventory");
        syncMixedAllocationRows();
        syncCopiedValueFields();
        syncReceiptPreview();
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
