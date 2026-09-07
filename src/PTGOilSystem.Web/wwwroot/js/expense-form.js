(function () {
    "use strict";

    function readGroups(root) {
        var store = root.querySelector("[data-expense-operation-options]");
        var groups = {};

        if (!store) {
            return groups;
        }

        store.querySelectorAll("[data-expense-source-options]").forEach(function (source) {
            var key = source.getAttribute("data-expense-source-options") || "";
            if (!key) {
                return;
            }

            groups[key] = {
                label: source.getAttribute("data-expense-source-label") || "",
                items: Array.from(source.options).map(function (option) {
                    return {
                        value: option.value || "",
                        text: option.textContent || ""
                    };
                })
            };
        });

        return groups;
    }

    function initExpenseOperationLink(root) {
        var typeSelect = root.querySelector("[data-expense-operation-type]");
        var recordSelect = root.querySelector("[data-expense-operation-record]");
        var recordLabel = root.querySelector("[data-expense-operation-record-label]");
        var groups = readGroups(root);

        if (!typeSelect || !recordSelect) {
            return;
        }

        if (root.dataset.expenseOperationReady === "true") {
            return;
        }

        var targets = {
            contract: root.querySelector("[data-expense-link-target='contract']"),
            shipment: root.querySelector("[data-expense-link-target='shipment']"),
            truckDispatch: root.querySelector("[data-expense-link-target='truckDispatch']"),
            transportLeg: root.querySelector("[data-expense-link-target='transportLeg']"),
            serviceProvider: root.querySelector("[data-expense-link-target='serviceProvider']"),
            operationalAsset: root.querySelector("[data-expense-link-target='operationalAsset']")
        };

        function addOption(value, text) {
            var option = document.createElement("option");
            option.value = value || "";
            option.textContent = text || "";
            recordSelect.appendChild(option);
        }

        function clearTargets() {
            Object.keys(targets).forEach(function (key) {
                if (targets[key]) {
                    targets[key].value = "";
                }
            });
        }

        function setTarget(type, value) {
            clearTargets();
            if (type && targets[type]) {
                targets[type].value = value || "";
            }
        }

        function populateRecordOptions(preferredValue) {
            var selectedType = typeSelect.value || "";
            var group = groups[selectedType];
            var items = group && Array.isArray(group.items) ? group.items : [];
            var hasType = !!group;

            recordSelect.innerHTML = "";
            addOption("", hasType ? "انتخاب مورد" : "اول ارتباط را انتخاب کنید");

            items.forEach(function (item) {
                addOption(item.value, item.text);
            });

            recordSelect.disabled = !hasType || items.length === 0;
            if (recordLabel) {
                recordLabel.textContent = hasType ? group.label : "مورد ثبت‌شده";
            }

            var canUsePreferred = preferredValue && items.some(function (item) {
                return item.value === preferredValue;
            });
            recordSelect.value = canUsePreferred ? preferredValue : "";
            setTarget(selectedType, recordSelect.value);
        }

        typeSelect.addEventListener("change", function () {
            populateRecordOptions("");
        });

        recordSelect.addEventListener("change", function () {
            setTarget(typeSelect.value || "", recordSelect.value || "");
        });

        root.dataset.expenseOperationReady = "true";
        populateRecordOptions(root.dataset.selectedValue || "");
    }

    function normalizeExpenseType(value) {
        return (value || "").trim().replace(/\s+/g, " ").toLocaleLowerCase();
    }

    function initExpenseTypeEntry(entry) {
        if (!entry || entry.dataset.expenseTypeReady === "true") {
            return;
        }

        var form = entry.closest("form");
        var typeIdField = form ? form.querySelector("[data-expense-type-id]") : null;
        var manualField = form ? form.querySelector("[data-expense-type-manual]") : null;
        var listId = entry.getAttribute("list");
        var list = listId ? document.getElementById(listId) : null;

        if (!form || !typeIdField || !manualField || !list) {
            return;
        }

        var knownTypes = Array.from(list.querySelectorAll("option[data-expense-type-id]"))
            .map(function (option) {
                return {
                    id: option.getAttribute("data-expense-type-id") || "",
                    text: option.value || ""
                };
            })
            .filter(function (item) {
                return item.id && item.text;
            });

        function syncExpenseTypeFields() {
            var typedValue = (entry.value || "").trim();
            var normalizedValue = normalizeExpenseType(typedValue);
            var matchedType = knownTypes.find(function (item) {
                return normalizeExpenseType(item.text) === normalizedValue;
            });

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

    // PTG-P1-04 — حساب نقدی فقط برای حالت «پرداخت‌شده» معنی دارد؛ در بقیهٔ حالت‌ها هم
    // پنهان می‌شود و هم خالی، وگرنه سندِ بدهی با یک حسابِ نقدیِ ولگرد ذخیره می‌شد و
    // اعتبارسنجِ سرور آن را رد می‌کرد بی‌آنکه کاربر بفهمد کدام فیلد اضافه است.
    var PAID_IMMEDIATELY = "2";

    function initExpenseSettlement(root) {
        if (root.dataset.expenseSettlementReady === "true") {
            return;
        }

        var mode = root.querySelector("[data-expense-settlement-mode]");
        var counterpartyGroup = root.querySelector("[data-expense-settlement-counterparty]");
        var cashGroup = root.querySelector("[data-expense-settlement-cash]");
        if (!mode || !counterpartyGroup || !cashGroup) {
            return;
        }

        var cashSelect = cashGroup.querySelector("select");

        function syncCashVisibility() {
            var isPaid = mode.value === PAID_IMMEDIATELY;
            var isPayable = mode.value === "1";
            counterpartyGroup.classList.toggle("d-none", !isPayable);
            cashGroup.classList.toggle("d-none", !isPaid);
            if (!isPaid && cashSelect) {
                cashSelect.value = "";
            }
        }

        mode.addEventListener("change", syncCashVisibility);
        root.dataset.expenseSettlementReady = "true";
        syncCashVisibility();
    }

    function initExpenseForms() {
        document.querySelectorAll("[data-expense-type-entry]").forEach(initExpenseTypeEntry);
        document.querySelectorAll("[data-expense-operation-link]").forEach(initExpenseOperationLink);
        document.querySelectorAll("[data-expense-settlement]").forEach(initExpenseSettlement);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initExpenseForms);
    } else {
        initExpenseForms();
    }

    document.addEventListener("ptg:page-ready", initExpenseForms);
})();
