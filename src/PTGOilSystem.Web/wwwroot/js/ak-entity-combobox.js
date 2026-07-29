(function () {
    "use strict";

    var selector = "select[data-ak-entity-combobox]";
    var sequence = 0;

    function text(value) {
        return (value || "").replace(/\s+/g, " ").trim();
    }

    function initials(value) {
        var parts = text(value).split(" ").filter(Boolean);
        if (!parts.length) return "—";
        return parts.slice(0, 2).map(function (part) { return part.charAt(0); }).join("").toUpperCase();
    }

    function enhance(select) {
        if (!select || select.dataset.akEntityReady === "true" || select.multiple) return;

        select.dataset.akEntityReady = "true";
        select.classList.add("ak-entity-native");

        var root = document.createElement("div");
        root.className = "ak-entity-combobox";
        root.dataset.open = "false";
        root.dataset.empty = "false";

        var input = document.createElement("input");
        input.type = "text";
        input.className = "ak-entity-input";
        input.autocomplete = "off";
        input.setAttribute("role", "combobox");
        input.setAttribute("aria-autocomplete", "list");
        input.setAttribute("aria-expanded", "false");
        input.setAttribute("aria-label", select.getAttribute("aria-label") || select.dataset.akLabel || (document.documentElement.lang === "en" ? "Search and select" : "جستجو و انتخاب"));
        input.placeholder = select.dataset.akPlaceholder || (document.documentElement.lang === "en" ? "Type to search..." : "برای جستجو بنویسید...");

        var chevron = document.createElement("span");
        chevron.className = "ak-entity-chevron";
        chevron.setAttribute("aria-hidden", "true");
        chevron.innerHTML = '<svg viewBox="0 0 16 16" width="14" height="14"><path d="M3.5 6 8 10.5 12.5 6" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>';

        // Selected value renders as a soft indigo chip inside the field shell
        // (field background stays white). Purely presentational; the native
        // <select> remains the single binding + event source.
        var selection = document.createElement("div");
        selection.className = "ak-entity-selection";
        selection.setAttribute("aria-hidden", "true");

        var menu = document.createElement("div");
        menu.className = "ak-entity-menu";
        menu.id = "ak-entity-list-" + (++sequence);
        menu.setAttribute("role", "listbox");
        input.setAttribute("aria-controls", menu.id);

        var options = document.createElement("div");
        options.className = "ak-entity-options";

        var empty = document.createElement("div");
        empty.className = "ak-entity-empty";
        empty.textContent = select.dataset.akEmpty || (document.documentElement.lang === "en" ? "No matching item" : "موردی پیدا نشد");

        var quickCreate = document.createElement("button");
        quickCreate.type = "button";
        quickCreate.className = "ak-entity-quick-create";
        quickCreate.textContent = "+ " + (select.dataset.akQuickCreateLabel || (document.documentElement.lang === "en" ? "New item" : "ثبت مورد جدید"));
        quickCreate.hidden = !select.dataset.akQuickCreateTarget && !select.dataset.akQuickCreateUrl;

        menu.appendChild(options);
        menu.appendChild(empty);
        menu.appendChild(quickCreate);
        select.parentNode.insertBefore(root, select);
        root.appendChild(select);
        root.appendChild(input);
        root.appendChild(selection);
        root.appendChild(chevron);
        root.appendChild(menu);

        var activeIndex = -1;

        function enabledOptions() {
            return Array.prototype.slice.call(select.options).filter(function (option) {
                return option.value && !option.disabled;
            });
        }

        function close(restore) {
            root.dataset.open = "false";
            input.setAttribute("aria-expanded", "false");
            activeIndex = -1;
            if (restore !== false) syncInput();
        }

        function open() {
            if (select.disabled) return;
            root.dataset.open = "true";
            input.setAttribute("aria-expanded", "true");
            input.value = "";
            input.placeholder = placeholderText();
            renderSelection("");
            render("");
        }

        function placeholderText() {
            return select.dataset.akPlaceholder || (document.documentElement.lang === "en" ? "Type to search..." : "برای جستجو بنویسید...");
        }

        function clearSelection() {
            if (select.disabled) return;
            var empty = Array.prototype.slice.call(select.options).find(function (option) { return !option.value; });
            select.value = empty ? empty.value : "";
            select.dispatchEvent(new Event("input", { bubbles: true }));
            select.dispatchEvent(new Event("change", { bubbles: true }));
        }

        function renderSelection(label) {
            selection.innerHTML = "";
            if (!label) {
                root.dataset.selected = "false";
                return;
            }
            root.dataset.selected = "true";
            var chip = document.createElement("span");
            chip.className = "ak-entity-chip";
            var span = document.createElement("span");
            span.className = "ak-entity-chip-label";
            span.textContent = label;
            chip.appendChild(span);
            if (!select.disabled) {
                var remove = document.createElement("button");
                remove.type = "button";
                remove.className = "ak-entity-chip-x";
                remove.tabIndex = -1;
                remove.setAttribute("aria-label", document.documentElement.lang === "en" ? "Clear" : "پاک کردن");
                remove.innerHTML = "&times;";
                remove.addEventListener("mousedown", function (event) { event.preventDefault(); });
                remove.addEventListener("click", function (event) {
                    event.stopPropagation();
                    clearSelection();
                    input.focus();
                });
                chip.appendChild(remove);
            }
            selection.appendChild(chip);
        }

        function syncInput() {
            var selected = select.options[select.selectedIndex];
            var label = selected && selected.value ? text(selected.textContent) : "";
            // The chip carries the selected label; the input stays empty so it is
            // free to type a search query. Hide the placeholder while a chip shows.
            input.value = "";
            input.placeholder = label ? "" : placeholderText();
            input.disabled = select.disabled;
            root.dataset.disabled = select.disabled ? "true" : "false";
            renderSelection(root.dataset.open === "true" ? "" : label);
        }

        function choose(value) {
            if (select.value !== value) {
                select.value = value;
                select.dispatchEvent(new Event("input", { bubbles: true }));
                select.dispatchEvent(new Event("change", { bubbles: true }));
            }
            close(true);
            input.focus();
        }

        function setActive(index) {
            var buttons = Array.prototype.slice.call(options.querySelectorAll(".ak-entity-option:not([hidden])"));
            if (!buttons.length) {
                activeIndex = -1;
                return;
            }
            activeIndex = Math.max(0, Math.min(index, buttons.length - 1));
            buttons.forEach(function (button, buttonIndex) {
                button.classList.toggle("is-active", buttonIndex === activeIndex);
            });
            buttons[activeIndex].scrollIntoView({ block: "nearest" });
        }

        function render(query) {
            var normalized = text(query).toLocaleLowerCase();
            options.innerHTML = "";
            var matches = enabledOptions().filter(function (option) {
                var haystack = [option.textContent, option.dataset.meta, option.dataset.metadata, option.dataset.code].map(text).join(" ").toLocaleLowerCase();
                return !normalized || haystack.indexOf(normalized) >= 0;
            });

            matches.forEach(function (option) {
                var button = document.createElement("button");
                var title = text(option.textContent);
                var meta = text(option.dataset.meta || option.dataset.metadata || option.dataset.code);
                button.type = "button";
                button.className = "ak-entity-option";
                button.dataset.value = option.value;
                button.setAttribute("role", "option");
                button.setAttribute("aria-selected", option.selected ? "true" : "false");
                button.innerHTML = '<span class="ak-entity-avatar" aria-hidden="true"></span><span class="ak-entity-copy"><span class="ak-entity-title"></span><span class="ak-entity-meta"></span></span>';
                button.querySelector(".ak-entity-avatar").textContent = option.dataset.avatarText || initials(title);
                button.querySelector(".ak-entity-title").textContent = title;
                var metaNode = button.querySelector(".ak-entity-meta");
                metaNode.textContent = meta;
                metaNode.hidden = !meta;
                options.appendChild(button);
            });

            root.dataset.empty = matches.length ? "false" : "true";
            activeIndex = -1;
        }

        input.addEventListener("focus", open);
        input.addEventListener("click", function () {
            if (root.dataset.open !== "true") open();
        });
        input.addEventListener("input", function () {
            if (root.dataset.open !== "true") open();
            render(input.value);
        });
        input.addEventListener("keydown", function (event) {
            if (event.key === "Escape") {
                event.preventDefault();
                close(true);
                return;
            }
            if (event.key === "ArrowDown" || event.key === "ArrowUp") {
                event.preventDefault();
                if (root.dataset.open !== "true") open();
                setActive(activeIndex + (event.key === "ArrowDown" ? 1 : -1));
                return;
            }
            if (event.key === "Enter" && root.dataset.open === "true") {
                var buttons = options.querySelectorAll(".ak-entity-option:not([hidden])");
                if (activeIndex >= 0 && buttons[activeIndex]) {
                    event.preventDefault();
                    choose(buttons[activeIndex].dataset.value);
                }
            }
            if (event.key === "Tab") close(true);
        });

        options.addEventListener("mousedown", function (event) { event.preventDefault(); });
        options.addEventListener("click", function (event) {
            var option = event.target.closest(".ak-entity-option");
            if (option) choose(option.dataset.value);
        });

        quickCreate.addEventListener("mousedown", function (event) { event.preventDefault(); });
        quickCreate.addEventListener("click", function () {
            close(true);
            var target = select.dataset.akQuickCreateTarget;
            if (target) {
                var opener = document.querySelector('[data-entity-modal-open="' + target.replace(/^#/, "") + '"]');
                if (opener) opener.click();
                return;
            }
            if (select.dataset.akQuickCreateUrl) {
                if (window.PTG && typeof window.PTG.openPageModal === "function") {
                    window.PTG.openPageModal(select.dataset.akQuickCreateUrl, {
                        title: select.dataset.akQuickCreateLabel || quickCreate.textContent,
                        quickCreateSelect: select,
                        size: "compact"
                    });
                } else {
                    window.location.href = select.dataset.akQuickCreateUrl;
                }
            }
        });

        select.addEventListener("change", syncInput);
        select.addEventListener("focus", function () { input.focus(); });
        select.addEventListener("invalid", function () { root.classList.add("is-invalid"); });

        root._akClose = close;

        new MutationObserver(function () {
            render(root.dataset.open === "true" ? input.value : "");
            syncInput();
        }).observe(select, { childList: true, subtree: true, attributes: true, attributeFilter: ["disabled", "selected", "label", "data-meta", "data-metadata"] });

        render("");
        syncInput();
    }

    function scan(root) {
        if (!root) return;
        if (root.matches && root.matches(selector)) enhance(root);
        if (root.querySelectorAll) root.querySelectorAll(selector).forEach(enhance);
    }

    function start() {
        scan(document);
        document.addEventListener("pointerdown", function (event) {
            document.querySelectorAll('.ak-entity-combobox[data-open="true"]').forEach(function (root) {
                if (!root.contains(event.target) && typeof root._akClose === "function") root._akClose(true);
            });
        });
        new MutationObserver(function (records) {
            records.forEach(function (record) {
                record.addedNodes.forEach(scan);
            });
        }).observe(document.body, { childList: true, subtree: true });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", start, { once: true });
    } else {
        start();
    }
})();
