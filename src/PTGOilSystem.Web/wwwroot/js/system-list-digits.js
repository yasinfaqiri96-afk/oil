/*
 * PTG system list digits.
 * Display-only normalizer: keeps list numbers readable with English digits.
 */

(function () {
    "use strict";

    var DIGIT_MAP = {
        "۰": "0", "۱": "1", "۲": "2", "۳": "3", "۴": "4",
        "۵": "5", "۶": "6", "۷": "7", "۸": "8", "۹": "9",
        "٠": "0", "١": "1", "٢": "2", "٣": "3", "٤": "4",
        "٥": "5", "٦": "6", "٧": "7", "٨": "8", "٩": "9",
        "٬": ",", "٫": "."
    };

    var LIST_SELECTOR = [
        ".ak-table-wrap",
        ".table-responsive",
        ".ak-list",
        "table.ak-table"
    ].join(",");

    var SKIP_SELECTOR = [
        "input",
        "textarea",
        "select",
        "option",
        "script",
        "style",
        "code",
        "pre",
        "[contenteditable='true']"
    ].join(",");

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init, { once: true });
    } else {
        init();
    }

    function init() {
        normalizeSystemListDigits(document);
        observeListChanges();
    }

    function normalizeDigits(value) {
        return String(value || "").replace(/[۰-۹٠-٩٬٫]/g, function (char) {
            return DIGIT_MAP[char] || char;
        });
    }

    function normalizeSystemListDigits(root) {
        var scope = root && root.querySelectorAll ? root : document;
        var parentList = scope.closest ? scope.closest(LIST_SELECTOR) : null;

        if (scope.matches && scope.matches(LIST_SELECTOR)) {
            normalizeElement(scope);
        }

        if (parentList) {
            normalizeElement(parentList);
        }

        scope.querySelectorAll(LIST_SELECTOR).forEach(normalizeElement);
    }

    function normalizeElement(element) {
        if (!element) return;

        normalizeAttribute(element, "data-column-label");

        element.querySelectorAll("[data-column-label]").forEach(function (node) {
            normalizeAttribute(node, "data-column-label");
        });

        walkTextNodes(element);
    }

    function normalizeAttribute(element, name) {
        var current = element.getAttribute(name);
        var normalized;

        if (!current) return;

        normalized = normalizeDigits(current);
        if (normalized !== current) element.setAttribute(name, normalized);
    }

    function walkTextNodes(root) {
        var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
            acceptNode: function (node) {
                if (!node.nodeValue || !/[۰-۹٠-٩٬٫]/.test(node.nodeValue)) return NodeFilter.FILTER_REJECT;
                if (node.parentElement && node.parentElement.closest(SKIP_SELECTOR)) return NodeFilter.FILTER_REJECT;
                return NodeFilter.FILTER_ACCEPT;
            }
        });

        var nodes = [];
        var node;

        while ((node = walker.nextNode())) nodes.push(node);

        nodes.forEach(function (textNode) {
            textNode.nodeValue = normalizeDigits(textNode.nodeValue);
        });
    }

    // فقط خودِ شاخهٔ تازه‌اضافه‌شده نرمال می‌شود. نسخهٔ قبلی برای هر گره، جدولِ والد را
    // با closest پیدا می‌کرد و کل جدول را دوباره می‌پیمود؛ روی جدول‌های بزرگ (مثل ثبت
    // بارگیری با ۱۰۰ ردیف) این کار به ازای هر گره تکرار می‌شد و صفحه ده‌ها ثانیه قفل می‌ماند.
    function normalizeAddedNode(node) {
        if (node.matches && node.matches(SKIP_SELECTOR)) return;

        if (node.closest && node.closest(LIST_SELECTOR)) {
            normalizeElement(node);
            return;
        }

        if (node.matches && node.matches(LIST_SELECTOR)) {
            normalizeElement(node);
        }

        if (node.querySelectorAll) {
            node.querySelectorAll(LIST_SELECTOR).forEach(normalizeElement);
        }
    }

    function observeListChanges() {
        if (!window.MutationObserver) return;

        // گره‌ها در یک دسته پردازش می‌شوند تا رندرِ یکبارهٔ چند صد ردیف، چند صد پیمایش جدا نسازد.
        var pending = [];
        var scheduled = false;

        function flush() {
            scheduled = false;
            var nodes = pending;
            pending = [];

            // مقایسهٔ جفتی فقط در دسته‌های کوچک؛ در دسته‌های بزرگ خودِ هزینهٔ مقایسه از پیمایش بیشتر می‌شود.
            var dedupe = nodes.length <= 50;

            nodes.forEach(function (node) {
                if (!node.isConnected) return;
                if (dedupe) {
                    // اگر والدِ همین گره در همین دسته پردازش شده، دوباره پیمایش لازم نیست.
                    var covered = nodes.some(function (other) {
                        return other !== node && other.contains && other.contains(node);
                    });
                    if (covered) return;
                }
                normalizeAddedNode(node);
            });
        }

        var observer = new MutationObserver(function (mutations) {
            mutations.forEach(function (mutation) {
                mutation.addedNodes.forEach(function (node) {
                    if (node.nodeType !== 1) return;
                    pending.push(node);
                });
            });

            if (pending.length === 0 || scheduled) return;
            scheduled = true;
            if (window.requestAnimationFrame) {
                window.requestAnimationFrame(flush);
            } else {
                window.setTimeout(flush, 0);
            }
        });

        observer.observe(document.body, { childList: true, subtree: true });
    }

    window.PTG = window.PTG || {};
    window.PTG.normalizeSystemListDigits = normalizeSystemListDigits;
    window.PTG.normalizeDigits = normalizeDigits;
})();
