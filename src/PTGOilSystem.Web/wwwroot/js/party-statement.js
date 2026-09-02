(function () {
    "use strict";

    // spa-nav با هر ناوبری، اسکریپت‌های #ptg-page-scripts را دوباره اجرا می‌کند و
    // syncPageAssets هم یک نسخه از این فایل را تزریق می‌کند. شنونده‌ها روی document
    // (delegated) هستند و یک‌بار بستن کافی است؛ بدون این نگهبان، هر کلیک «جزئیات»
    // دوبار پردازش می‌شد (باز و بلافاصله بسته) و «چاپ» دو پنجره باز می‌کرد.
    if (window.__ptgPartyStatementBound) {
        return;
    }
    window.__ptgPartyStatementBound = true;

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

    // واگذاری رویداد روی document: صورت‌حساب هم در صفحهٔ مستقل و هم داخل تب پروفایل
    // (که بعد از DOMContentLoaded تزریق می‌شود) رندر می‌شود؛ با bind مستقیم، دکمهٔ
    // «جزئیات» در حالت تزریقی هیچ شنونده‌ای نداشت و باز نمی‌شد.
    document.addEventListener("click", function (event) {
        var detailsButton = event.target.closest("[data-statement-details]");
        if (detailsButton) {
            toggleContractDetails(detailsButton);
            return;
        }

        var printButton = event.target.closest("[data-statement-print]");
        if (!printButton) {
            return;
        }
        var printUrl = printButton.getAttribute("data-print-url");
        if (printUrl) {
            window.open(printUrl, "_blank", "noopener");
            return;
        }
        window.print();
    });

    // انتخاب تأمین‌کننده: مقدار هر option نشانی صورت‌حساب همان تأمین‌کننده با فیلترهای
    // فعلی است؛ فقط به آن نشانی می‌رویم و هیچ فیلتری اینجا ساخته نمی‌شود.
    document.addEventListener("change", function (event) {
        var select = event.target.closest("[data-statement-party-switch]");
        if (!select || !select.value) {
            return;
        }
        window.location.href = select.value;
    });

    document.addEventListener("DOMContentLoaded", function () {
        if (document.querySelector('[data-statement-auto-print="true"]')) {
            window.setTimeout(function () { window.print(); }, 150);
        }
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
