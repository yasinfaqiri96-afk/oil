(function () {
    "use strict";

    // نمای «قراردادها»: جزئیات هر قرارداد فقط هنگام کلیک (lazy) از سرور گرفته می‌شود.
    function toggleContractDetails(button) {
        var detailsRow = button.closest("tr") ? button.closest("tr").nextElementSibling : null;
        if (!detailsRow || !detailsRow.classList.contains("statement-details-row")) {
            return;
        }

        var expanded = button.getAttribute("aria-expanded") === "true";
        if (expanded) {
            detailsRow.hidden = true;
            button.setAttribute("aria-expanded", "false");
            button.classList.remove("is-open");
            return;
        }

        detailsRow.hidden = false;
        button.setAttribute("aria-expanded", "true");
        button.classList.add("is-open");

        var slot = detailsRow.querySelector("[data-details-slot]");
        if (!slot || slot.getAttribute("data-loaded") === "true") {
            return;
        }

        var url = button.getAttribute("data-details-url");
        if (!url) {
            return;
        }

        slot.innerHTML = "<div class='statement-details-loading'>در حال بارگذاری…</div>";
        fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error("HTTP " + response.status);
                }
                return response.text();
            })
            .then(function (html) {
                slot.innerHTML = html;
                slot.setAttribute("data-loaded", "true");
            })
            .catch(function () {
                slot.innerHTML = "<div class='statement-details-loading'>بارگذاری جزئیات ناموفق بود.</div>";
            });
    }

    document.addEventListener("DOMContentLoaded", function () {
        document.querySelectorAll("[data-statement-details]").forEach(function (button) {
            button.addEventListener("click", function () {
                toggleContractDetails(button);
            });
        });
    });

    // صفحه‌بندی داخل محتوای lazy نیز بدون بستن گروه، همان slot را تازه می‌کند.
    document.addEventListener("click", function (event) {
        var button = event.target.closest("[data-statement-details-page]");
        if (!button) {
            return;
        }
        var slot = button.closest("[data-details-slot]");
        var url = button.getAttribute("data-statement-details-page");
        if (!slot || !url) {
            return;
        }
        slot.innerHTML = "<div class='statement-details-loading'>در حال بارگذاری…</div>";
        fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error("HTTP " + response.status);
                }
                return response.text();
            })
            .then(function (html) {
                slot.innerHTML = html;
            })
            .catch(function () {
                slot.innerHTML = "<div class='statement-details-loading'>بارگذاری جزئیات ناموفق بود.</div>";
            });
    });
})();
