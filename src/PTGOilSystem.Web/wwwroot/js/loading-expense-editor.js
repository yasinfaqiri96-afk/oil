(function () {
    window.PTG = window.PTG || {};

    const CALC_PER_MT = "2";
    const PARTY_NONE = "0";
    const PARTY_PROVIDER = "1";
    const PARTY_ASSET = "2";

    function parseNumber(input) {
        if (!input) {
            return 0;
        }
        return parseFloat(input.value || "0") || 0;
    }

    function formatMoney(value) {
        return `${value.toFixed(2)} USD`;
    }

    function ensureRequestAlert(form) {
        if (!form) {
            return null;
        }

        let alert = form.querySelector("[data-loading-expense-request-alert]");
        if (alert) {
            return alert;
        }

        alert = document.createElement("div");
        alert.className = "alert alert-danger mb-0 d-none";
        alert.setAttribute("data-loading-expense-request-alert", "true");

        const summary = form.querySelector("[asp-validation-summary], [data-valmsg-summary], .alert.alert-danger");
        if (summary) {
            summary.insertAdjacentElement("afterend", alert);
        } else {
            form.insertAdjacentElement("afterbegin", alert);
        }

        return alert;
    }

    function hideRequestAlert(form) {
        const alert = form?.querySelector("[data-loading-expense-request-alert]");
        if (!alert) {
            return;
        }

        alert.textContent = "";
        alert.classList.add("d-none");
    }

    function showRequestAlert(form, message) {
        const alert = ensureRequestAlert(form);
        if (!alert) {
            return;
        }

        alert.textContent = message;
        alert.classList.remove("d-none");
    }

    function extractEditorMarkup(html) {
        if (!html || !html.trim()) {
            return null;
        }

        const parsed = new DOMParser().parseFromString(html, "text/html");
        const editor = parsed.querySelector("[data-loading-expense-editor]");
        return editor ? editor.outerHTML : null;
    }

    function newToken() {
        if (window.crypto && typeof window.crypto.randomUUID === "function") {
            return "n" + window.crypto.randomUUID().replace(/-/g, "");
        }
        return "n" + Date.now().toString(36) + Math.random().toString(36).slice(2);
    }

    function normalizeExpenseType(value) {
        return (value || "").trim().replace(/\s+/g, " ").toLocaleLowerCase();
    }

    function initializeEditor(root) {
        if (!root || root.dataset.loadingExpenseReady === "true") {
            return;
        }

        const form = root.querySelector("[data-loading-expense-form]");
        if (!form) {
            return;
        }

        root.dataset.loadingExpenseReady = "true";

        const loadedQuantity = parseFloat(form.querySelector("[data-loaded-quantity]")?.value || "0") || 0;
        const rowsContainer = form.querySelector("[data-expense-rows]");
        const template = form.querySelector("[data-row-template]");
        const addButton = form.querySelector("[data-add-row]");
        const emptyNote = form.querySelector("[data-rows-empty]");

        const summaryCount = root.querySelector("[data-summary-count]");
        const summaryNone = root.querySelector("[data-summary-none]");
        const summaryProvider = root.querySelector("[data-summary-provider]");
        const summaryAsset = root.querySelector("[data-summary-asset]");
        const summaryTotal = root.querySelector("[data-summary-total]");

        function initExpenseTypeEntry(row) {
            const entry = row.querySelector("[data-row-type-entry]");
            const typeIdField = row.querySelector("[data-row-type-id]");
            const manualField = row.querySelector("[data-row-type-manual]");
            const listId = entry?.getAttribute("list");
            const list = listId ? document.getElementById(listId) : null;

            if (!entry || !typeIdField || !manualField || !list || entry.dataset.expenseTypeReady === "true") {
                return;
            }

            const knownTypes = Array.from(list.querySelectorAll("option[data-expense-type-id]"))
                .map((option) => ({
                    id: option.getAttribute("data-expense-type-id") || "",
                    text: option.value || ""
                }))
                .filter((item) => item.id && item.text);

            function syncExpenseTypeFields() {
                const typedValue = (entry.value || "").trim();
                const normalizedValue = normalizeExpenseType(typedValue);
                const matchedType = knownTypes.find((item) => normalizeExpenseType(item.text) === normalizedValue);

                if (!typedValue) {
                    typeIdField.value = "";
                    manualField.value = "";
                    return;
                }

                if (matchedType) {
                    typeIdField.value = matchedType.id;
                    manualField.value = "";
                    return;
                }

                typeIdField.value = "";
                manualField.value = typedValue;
            }

            entry.addEventListener("input", syncExpenseTypeFields);
            entry.addEventListener("change", syncExpenseTypeFields);
            form.addEventListener("submit", syncExpenseTypeFields);
            entry.dataset.expenseTypeReady = "true";
            syncExpenseTypeFields();
        }

        function applyCalcMode(row) {
            const calc = row.querySelector("[data-row-calc]");
            const qty = row.querySelector("[data-row-qty]");
            const rate = row.querySelector("[data-row-rate]");
            const amount = row.querySelector("[data-row-amount]");
            if (!calc || !qty || !rate || !amount) {
                return;
            }

            const perMt = calc.value === CALC_PER_MT;
            qty.disabled = !perMt;
            rate.disabled = !perMt;
            amount.readOnly = perMt;

            if (perMt) {
                if (!qty.value && loadedQuantity > 0) {
                    qty.value = loadedQuantity;
                }
                const computed = parseNumber(qty) * parseNumber(rate);
                amount.value = computed > 0 ? computed.toFixed(4) : "";
            }
        }

        function applyPartyType(row) {
            const party = row.querySelector("[data-row-party]");
            const provider = row.querySelector("[data-row-provider]");
            const asset = row.querySelector("[data-row-asset]");
            const noParty = row.querySelector("[data-row-noparty]");
            if (!party) {
                return;
            }

            const isProvider = party.value === PARTY_PROVIDER;
            const isAsset = party.value === PARTY_ASSET;

            if (provider) {
                provider.disabled = !isProvider;
                provider.hidden = !isProvider;
                if (!isProvider) {
                    provider.value = "";
                }
            }
            if (asset) {
                asset.disabled = !isAsset;
                asset.hidden = !isAsset;
                if (!isAsset) {
                    asset.value = "";
                }
            }
            if (noParty) {
                noParty.hidden = isProvider || isAsset;
            }
        }

        function updateSummary() {
            const rows = rowsContainer ? Array.from(rowsContainer.querySelectorAll("[data-expense-row]")) : [];
            let none = 0;
            let provider = 0;
            let asset = 0;

            rows.forEach((row) => {
                const amount = parseNumber(row.querySelector("[data-row-amount]"));
                const party = row.querySelector("[data-row-party]")?.value || PARTY_NONE;
                if (party === PARTY_PROVIDER) {
                    provider += amount;
                } else if (party === PARTY_ASSET) {
                    asset += amount;
                } else {
                    none += amount;
                }
            });

            const grand = none + provider + asset;

            if (summaryCount) summaryCount.textContent = rows.length.toString();
            if (summaryNone) summaryNone.textContent = formatMoney(none);
            if (summaryProvider) summaryProvider.textContent = formatMoney(provider);
            if (summaryAsset) summaryAsset.textContent = formatMoney(asset);
            if (summaryTotal) summaryTotal.textContent = formatMoney(grand);
            if (emptyNote) emptyNote.classList.toggle("d-none", rows.length > 0);

            updateAllocationPreview(grand);
        }

        function updateAllocationPreview(grandTotal) {
            const section = root.querySelector("[data-expense-alloc]");
            const allocRows = section ? Array.from(section.querySelectorAll("[data-alloc-row]")) : [];
            if (allocRows.length === 0) {
                return;
            }
            // پایه = مصارفِ قبلاً‌ثبت‌شدهٔ این گروه + مجموعِ ردیف‌های در حال ورود.
            const base = parseFloat(section.getAttribute("data-alloc-base") || "0") || 0;
            const total = base + grandTotal;
            let allocated = 0;
            allocRows.forEach((row, index) => {
                const share = parseFloat(row.getAttribute("data-alloc-share") || "0") || 0;
                const cell = row.querySelector("[data-alloc-amount]");
                const amount = index === allocRows.length - 1
                    ? total - allocated
                    : Math.round(total * share) / 100;
                allocated += amount;
                if (cell) {
                    cell.textContent = formatMoney(amount);
                }
            });
        }

        function wireRow(row) {
            const calc = row.querySelector("[data-row-calc]");
            const qty = row.querySelector("[data-row-qty]");
            const rate = row.querySelector("[data-row-rate]");
            const amount = row.querySelector("[data-row-amount]");
            const party = row.querySelector("[data-row-party]");
            const removeBtn = row.querySelector("[data-row-remove]");

            calc?.addEventListener("change", () => { applyCalcMode(row); updateSummary(); });
            qty?.addEventListener("input", () => { applyCalcMode(row); updateSummary(); });
            rate?.addEventListener("input", () => { applyCalcMode(row); updateSummary(); });
            amount?.addEventListener("input", updateSummary);
            party?.addEventListener("change", () => { applyPartyType(row); updateSummary(); });
            removeBtn?.addEventListener("click", () => {
                row.remove();
                updateSummary();
            });

            initExpenseTypeEntry(row);
            applyCalcMode(row);
            applyPartyType(row);
        }

        function addRow() {
            if (!template || !rowsContainer) {
                return;
            }
            const token = newToken();
            const html = template.innerHTML.replace(/__INDEX__/g, token);
            const wrapper = document.createElement("template");
            wrapper.innerHTML = html.trim();
            const row = wrapper.content.querySelector("[data-expense-row]");
            if (!row) {
                return;
            }
            rowsContainer.appendChild(row);
            wireRow(row);
            updateSummary();
            row.querySelector("[data-row-type]")?.focus();
        }

        async function submitModalForm(event) {
            event.preventDefault();
            const submitButtons = Array.from(form.querySelectorAll("button[type='submit']"));
            submitButtons.forEach((b) => { b.disabled = true; });
            hideRequestAlert(form);

            try {
                const response = await fetch(form.action, {
                    method: form.method || "POST",
                    body: new FormData(form),
                    headers: {
                        "X-Requested-With": "XMLHttpRequest",
                        "Accept": "application/json, text/html"
                    }
                });
                const contentType = response.headers.get("content-type") || "";

                if (response.redirected && response.url) {
                    window.location.href = response.url;
                    return;
                }

                if (contentType.includes("application/json")) {
                    const result = await response.json();
                    if (result?.success) {
                        const modalElement = root.closest(".modal");
                        if (modalElement && window.bootstrap?.Modal) {
                            const instance = window.bootstrap.Modal.getInstance(modalElement)
                                || window.bootstrap.Modal.getOrCreateInstance(modalElement);
                            instance.hide();
                        }
                        window.location.href = result.redirectUrl || window.location.href;
                        return;
                    }

                    showRequestAlert(form, "ذخیره انجام نشد. لطفاً معلومات را دوباره بررسی کنید.");
                    return;
                }

                const text = await response.text();
                const container = root.parentElement;
                const markup = extractEditorMarkup(text);
                if (container && markup) {
                    container.innerHTML = markup;
                    container.querySelectorAll("[data-loading-expense-editor]").forEach(initializeEditor);
                    return;
                }

                throw new Error("Unexpected loading expense modal response.");
            } catch (_error) {
                showRequestAlert(form, "در ثبت مصارف مشکلی پیش آمد. فورم داخل مودال نگه داشته شد؛ لطفاً دوباره تلاش کنید.");
            } finally {
                submitButtons.forEach((b) => { b.disabled = false; });
            }
        }

        addButton?.addEventListener("click", addRow);
        if (rowsContainer) {
            rowsContainer.querySelectorAll("[data-expense-row]").forEach(wireRow);
        }
        // Any editor rendered inside a Bootstrap modal must submit via AJAX so it
        // never falls back to a full-page POST that navigates to /Loading/EditExpenses.
        const insideModal = !!root.closest(".modal");
        if (root.dataset.submitMode === "modal" || insideModal) {
            form.addEventListener("submit", submitModalForm);
        }

        updateSummary();
    }

    async function loadRemoteEditor(modal, url) {
        const body = modal?.querySelector("[data-loading-expense-remote-body]");
        if (!modal || !body || !url) {
            return;
        }

        modal.dataset.loadingExpenseUrl = url;
        modal.dataset.loadingExpenseFetching = url;
        if (!body.querySelector("[data-loading-expense-editor]")) {
            body.innerHTML = "<div class=\"ak-empty\"><div class=\"spinner-border spinner-border-sm text-primary\" role=\"status\" aria-hidden=\"true\"></div><span>در حال بارگذاری فرم مصارف...</span></div>";
        }

        try {
            const response = await fetch(url, {
                headers: { "X-Requested-With": "XMLHttpRequest" }
            });

            if (!response.ok) {
                throw new Error("Failed to load expense editor.");
            }

            const markup = extractEditorMarkup(await response.text());
            if (!markup) {
                throw new Error("Expense editor markup not found.");
            }

            body.innerHTML = markup;
            body.querySelectorAll("[data-loading-expense-editor]").forEach(initializeEditor);
            modal.dataset.loadingExpenseLoadedUrl = url;
        } catch (_error) {
            body.innerHTML = `<div class="alert alert-danger mb-0">بارگذاری فرم مصارف انجام نشد. <a href="${url}" class="alert-link">باز کردن صفحه</a></div>`;
        } finally {
            if (modal.dataset.loadingExpenseFetching === url) {
                delete modal.dataset.loadingExpenseFetching;
            }
        }
    }

    function resolveModalTriggerUrl(modal) {
        if (modal.id) {
            const trigger = document.querySelector(`[data-bs-target="#${modal.id}"][data-expense-url]`);
            if (trigger) {
                return trigger.getAttribute("data-expense-url");
            }
        }
        return modal.dataset.loadingExpenseUrl || null;
    }

    function initializeRemoteModal(modal) {
        if (!modal || modal.dataset.loadingExpenseRemoteReady === "true") {
            return;
        }

        modal.dataset.loadingExpenseRemoteReady = "true";
        modal.addEventListener("show.bs.modal", function (event) {
            const trigger = event.relatedTarget;
            const url = trigger?.getAttribute("data-expense-url") || modal.dataset.loadingExpenseUrl;
            if (!url) {
                return;
            }
            // فرم از قبل (prefetch) برای همین URL آماده است → بدون fetch دوباره، باز شدن آنی.
            const alreadyLoaded = modal.dataset.loadingExpenseLoadedUrl === url
                && modal.querySelector("[data-loading-expense-editor]");
            if (alreadyLoaded || modal.dataset.loadingExpenseFetching === url) {
                return;
            }
            loadRemoteEditor(modal, url);
        });

        // Details can explicitly defer the heavy editor until the modal is opened.
        if (modal.dataset.loadingExpensePrefetch !== "false") {
            const prefetchUrl = resolveModalTriggerUrl(modal);
            if (prefetchUrl) {
                loadRemoteEditor(modal, prefetchUrl);
            }
        }
    }

    function boot() {
        document.querySelectorAll("[data-loading-expense-editor]").forEach(initializeEditor);
        document.querySelectorAll("[data-loading-expense-remote-modal]").forEach(initializeRemoteModal);
    }

    window.PTG.initLoadingExpenseEditor = initializeEditor;
    window.PTG.loadRemoteLoadingExpenseEditor = loadRemoteEditor;

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", boot, { once: true });
    } else {
        boot();
    }
})();
