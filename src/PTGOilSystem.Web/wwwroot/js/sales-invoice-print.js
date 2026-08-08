/*
 * PTG sales invoice — screen-only toolbar behaviour.
 *
 * The print trigger lives here, never as an inline handler in the view: markup in
 * Views/ must stay free of print handlers (enforced by
 * TabularExportServiceTests.Views_Do_Not_Contain_Print_Buttons_Or_Print_Handlers),
 * and an external listener keeps the sheet itself pure document markup.
 *
 * "Save as PDF" is the same browser dialog as printing, so one trigger covers both.
 */

(function () {
    "use strict";

    function init() {
        document.querySelectorAll("[data-invoice-print]").forEach(function (trigger) {
            if (trigger.dataset.invoicePrintReady === "true") return;
            trigger.dataset.invoicePrintReady = "true";

            trigger.addEventListener("click", function (event) {
                event.preventDefault();
                window.print();
            });
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init, { once: true });
    } else {
        init();
    }
})();
