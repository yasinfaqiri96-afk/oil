/* Shared display behaviour for Create/Edit, direct navigation and SPA. */
(function () {
    "use strict";
    window.PTG = window.PTG || {};

    function init() {
        document.querySelectorAll("[data-ctc-transfer]").forEach(function (root) {
            if (root.dataset.ctcReady === "true") return;
            root.dataset.ctcReady = "true";
            function sync() {
                [["PayeeCustomerId", "ctc-payee-display"], ["PayerCustomerId", "ctc-payer-display"]].forEach(function (pair) {
                    var select = root.querySelector('[name="' + pair[0] + '"]');
                    var display = root.querySelector('[id="' + pair[1] + '"]');
                    if (select && display) display.value = select.value && select.selectedOptions.length
                        ? select.selectedOptions[0].textContent.trim() : "";
                });
                var amount = root.querySelector("[data-ctc-amount]");
                var currency = root.querySelector("[data-ctc-currency]");
                var amountMirror = root.querySelector("[data-ctc-amount-mirror]");
                var currencyMirror = root.querySelector("[data-ctc-currency-mirror]");
                if (amount && amountMirror) amountMirror.value = amount.value;
                if (currency && currencyMirror) currencyMirror.value = currency.value;
            }
            root.addEventListener("input", sync);
            root.addEventListener("change", sync);
            root.addEventListener("click", function (event) {
                var button = event.target.closest("[data-ctc-clear]");
                if (!button || !root.contains(button)) return;
                var select = root.querySelector('[name="' + button.dataset.ctcClear + '"]');
                if (!select) return;
                select.value = "";
                select.dispatchEvent(new Event("change", { bubbles: true }));
                select.focus();
            });
            var form = root.closest("form");
            if (form) form.addEventListener("reset", function () { window.setTimeout(sync, 0); });
            sync();
        });
    }

    if (!window.PTG.customerTransferBound) {
        window.PTG.customerTransferBound = true;
        window.addEventListener("ptg:page-ready", init);
        document.addEventListener("DOMContentLoaded", init, { once: true });
    }
    init();
})();
