// فیلد تأمین‌کنندهٔ قرارداد: افزودن/حذف «تأمین‌کنندهٔ دیگر» برای قرارداد چندتأمین‌کننده،
// با مقدار و قیمت فی تُن هر تأمین‌کننده و نمایش فوری جمع مقدار، ارزش و میانگین موزون.
// سرور همین محاسبه را دوباره انجام می‌دهد؛ این فقط پیش‌نمایش است.
(function () {
    "use strict";

    function reindex(list) {
        list.querySelectorAll("[data-extra-supplier-row]").forEach(function (row, index) {
            var select = row.querySelector("select");
            var qty = row.querySelector("[data-supplier-term-qty]");
            var price = row.querySelector("[data-supplier-term-price]");
            if (select) { select.name = "AdditionalSupplierIds[" + index + "]"; }
            if (qty) { qty.name = "AdditionalSupplierQuantitiesMt[" + index + "]"; }
            if (price) { price.name = "AdditionalSupplierUnitPricesUsd[" + index + "]"; }
        });
    }

    function number(input) {
        var value = input ? parseFloat(String(input.value || "").replace(",", ".")) : NaN;
        return isFinite(value) ? value : 0;
    }

    function format(value, digits) {
        return value.toLocaleString("en-US", { minimumFractionDigits: 0, maximumFractionDigits: digits });
    }

    function init(picker) {
        var list = picker.querySelector("[data-extra-supplier-list]");
        var template = picker.querySelector("[data-extra-supplier-template]");
        var addButton = picker.querySelector("[data-add-extra-supplier]");
        var primaryTerms = picker.querySelector("[data-supplier-terms-primary]");
        var summary = picker.querySelector("[data-supplier-terms-summary]");
        if (!list || !template || !addButton) {
            return;
        }

        var form = picker.closest("form");
        var contractQuantity = form ? form.querySelector("[name='QuantityMt']") : null;
        var contractPrice = form ? form.querySelector("[name='FinalPriceUsdPerMt']") : null;

        function refresh() {
            var multi = list.querySelectorAll("[data-extra-supplier-row]").length > 0;
            if (primaryTerms) {
                primaryTerms.hidden = !multi;
                primaryTerms.querySelectorAll("input").forEach(function (input) { input.disabled = !multi; });
            }

            if (!summary) {
                return;
            }

            if (!multi) {
                summary.hidden = true;
                summary.textContent = "";
                return;
            }

            var rows = [primaryTerms].concat(Array.from(list.querySelectorAll("[data-extra-supplier-row]")));
            var totalQty = 0;
            var totalValue = 0;
            rows.forEach(function (row) {
                if (!row) { return; }
                var qty = number(row.querySelector("[data-supplier-term-qty]"));
                var price = number(row.querySelector("[data-supplier-term-price]"));
                totalQty += qty;
                totalValue += qty * price;
            });

            var average = totalQty > 0 ? totalValue / totalQty : 0;
            summary.hidden = false;
            summary.textContent = "مجموع قرارداد: " + format(totalQty, 4) + " تُن · ارزش خرید: "
                + format(totalValue, 2) + " USD · میانگین موزون: " + format(average, 4) + " USD/تُن";

            if (totalQty > 0) {
                if (contractQuantity) { contractQuantity.value = String(Math.round(totalQty * 10000) / 10000); }
                if (contractPrice) { contractPrice.value = String(Math.round(average * 10000) / 10000); }
            }
        }

        addButton.addEventListener("click", function () {
            var index = list.querySelectorAll("[data-extra-supplier-row]").length;
            list.insertAdjacentHTML("beforeend", template.innerHTML.split("__index__").join(String(index)));
            var added = list.lastElementChild ? list.lastElementChild.querySelector("select") : null;
            if (added) {
                added.focus();
            }
            refresh();
        });

        list.addEventListener("click", function (event) {
            var button = event.target.closest("[data-remove-extra-supplier]");
            if (!button) {
                return;
            }

            var row = button.closest("[data-extra-supplier-row]");
            if (row) {
                row.remove();
                reindex(list);
                refresh();
            }
        });

        picker.addEventListener("input", function (event) {
            if (event.target.matches("[data-supplier-term-qty], [data-supplier-term-price]")) {
                refresh();
            }
        });

        refresh();
    }

    function boot() {
        document.querySelectorAll("[data-contract-supplier-picker]").forEach(init);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", boot);
    } else {
        boot();
    }
})();
