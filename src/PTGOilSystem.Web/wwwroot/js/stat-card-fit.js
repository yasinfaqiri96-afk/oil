(function () {
    "use strict";

    var valueSelector = "[data-ak-stat-value]";
    var observedContents = new WeakSet();
    var resizeObserver = typeof ResizeObserver === "function"
        ? new ResizeObserver(function (entries) {
            entries.forEach(function (entry) {
                fitValue(entry.target.querySelector(valueSelector));
            });
        })
        : null;

    function fitValue(value) {
        if (!value || !value.isConnected) return;

        // Always measure from the component's normal density tier so a wider
        // card can restore the intended size after a responsive resize.
        value.style.removeProperty("font-size");

        var availableWidth = value.clientWidth;
        if (availableWidth <= 0 || value.scrollWidth <= availableWidth + 0.5) return;

        var fontSize = parseFloat(window.getComputedStyle(value).fontSize);
        if (!Number.isFinite(fontSize) || fontSize <= 0) return;

        // Use the measured ratio rather than fixed character thresholds. The
        // second pass absorbs font rounding and keeps every prepared digit in
        // the card without changing its text or accounting value.
        for (var pass = 0; pass < 2 && value.scrollWidth > value.clientWidth + 0.5; pass += 1) {
            fontSize *= value.clientWidth / value.scrollWidth * 0.98;
            value.style.fontSize = Math.max(1, fontSize).toFixed(2) + "px";
        }
    }

    function init(root) {
        (root || document).querySelectorAll(valueSelector).forEach(function (value) {
            fitValue(value);

            var content = value.closest(".ak-stat-card__content");
            if (resizeObserver && content && !observedContents.has(content)) {
                observedContents.add(content);
                resizeObserver.observe(content);
            }
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", function () { init(document); }, { once: true });
    } else {
        init(document);
    }

    window.addEventListener("ptg:page-ready", function () { init(document); });

    if (document.fonts && document.fonts.ready) {
        document.fonts.ready.then(function () { init(document); });
    }

    if (!resizeObserver) {
        window.addEventListener("resize", function () { init(document); });
    }
})();
