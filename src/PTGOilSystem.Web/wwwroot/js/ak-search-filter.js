/*
 * PTG Oil System — shared Search/Filter owner (groups A & B).
 *
 * Single source of truth for the Akaunting-style search bar. Mounts on every
 * `[data-ak-filter]` host. Free-text search submits the GET form natively, so
 * the existing server-side search param / pagination / sorting stay intact.
 * No localStorage: the server round-trip is the state, avoiding UI/query drift.
 *
 * Two modes, driven by markup:
 *   - Search-only (group A): no config -> popover shows a "Search for X" row.
 *   - Search + filters (group B): a <script data-ak-filter-config> lists the
 *     structured filters. Picking a field -> value writes hidden inputs with the
 *     EXACT existing query param names and submits the GET form. Chips are
 *     server-rendered from the applied params; removing one drops the params and
 *     resubmits. The chrome changes, the request contract does not.
 *
 * A filter marked `multiple` keeps several values under the SAME param name
 * (key=1&key=2&key=5) - OR inside one filter, AND between filters, which is
 * exactly what repeated query params + array.Contains(column) produce.
 */
(function () {
    "use strict";

    var isEn = (document.documentElement.getAttribute("lang") || "fa").toLowerCase().indexOf("en") === 0;
    function T(fa, en) { return isEn ? en : fa; }

    function esc(s) {
        return String(s == null ? "" : s)
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;").replace(/'/g, "&#039;");
    }

    function submitForm(form) {
        if (!form) return;
        if (typeof form.requestSubmit === "function") form.requestSubmit();
        else form.submit();
    }

    function mount(root) {
        if (root.dataset.akFilterBound === "1") return;
        root.dataset.akFilterBound = "1";

        var clientMode = root.hasAttribute("data-ak-filter-client");
        var form = root.matches("form") ? root : root.querySelector("form");
        var stateHost = clientMode ? root : form;
        var input = root.querySelector("[data-ak-filter-input]");
        var clearBtn = root.querySelector("[data-ak-filter-clear]");
        var popover = root.querySelector("[data-ak-filter-popover]");
        if (!input || !stateHost) return;

        // ---- Filter config (group B only) ----
        var fields = [];
        var cfgEl = root.querySelector("[data-ak-filter-config]");
        if (cfgEl) { try { fields = JSON.parse(cfgEl.textContent) || []; } catch (e) { fields = []; } }
        var hasFilters = fields.length > 0;

        var nav = { state: "field", field: null }; // field | value | date

        function appliedKeys() {
            return Array.prototype.map.call(
                stateHost.querySelectorAll("[data-ak-filter-param]"),
                function (el) { return el.getAttribute("data-ak-filter-param"); });
        }
        function availableFields(query) {
            var used = appliedKeys();
            var q = (query || "").trim().toLocaleLowerCase();
            return fields.filter(function (f) {
                // فیلتر چندانتخابی پس از اعمال هم در فهرست می‌ماند تا بتوان مقدار افزود.
                return (f.multiple || used.indexOf(f.key) === -1) &&
                    (!q || String(f.label).toLocaleLowerCase().indexOf(q) !== -1);
            });
        }

        function ownedInputs(key) {
            return Array.prototype.slice.call(
                stateHost.querySelectorAll('[data-ak-filter-param="' + cssEsc(key) + '"]'));
        }
        function appliedValues(key) {
            return ownedInputs(key).map(function (el) { return el.value; });
        }

        function hasActiveFilters() { return stateHost.querySelectorAll("[data-ak-filter-param]").length > 0; }
        function syncClear() {
            if (clearBtn) clearBtn.hidden = !(input.value.trim() || hasActiveFilters());
        }

        function optionLabel(f, value) {
            var option = (f.options || []).filter(function (o) { return o.value === value; })[0];
            return option ? option.label : value;
        }

        function syncClientChips() {
            if (!clientMode) return;
            var chips = root.querySelector("[data-ak-filter-chips]");
            if (!chips) return;
            chips.innerHTML = fields.map(function (f) {
                var owned = Array.prototype.slice.call(
                    stateHost.querySelectorAll('[data-ak-filter-param="' + cssEsc(f.key) + '"]'));
                if (!owned.length) return "";
                var first = owned[0].value || "";
                var valueLabel = first;
                if (f.type === "daterange") {
                    valueLabel = first + (owned[1] ? " → " + owned[1].value : "");
                } else {
                    valueLabel = optionLabel(f, first);
                    if (f.multiple && owned.length > 1) valueLabel += " +" + (owned.length - 1);
                }
                return '<span class="ak-chip-group" data-ak-chip="' + esc(f.key) + '">' +
                    '<span class="ak-chip">' + esc(f.label) + '</span>' +
                    '<span class="ak-chip"><span>' + esc(valueLabel) + '</span>' +
                    '<button type="button" class="ak-chip-remove" data-ak-remove-chip="' + esc(f.key) +
                    '" aria-label="' + T("حذف فیلتر", "Remove filter") + '">&times;</button></span></span>';
            }).join("");
        }

        function applyChanges() {
            syncClientChips();
            syncClear();
            if (!clientMode) {
                submitForm(form);
                return;
            }
            var values = {};
            stateHost.querySelectorAll("[data-ak-filter-param]").forEach(function (el) {
                // چندمقداری: مقدارها زیر همان نام جمع می‌شوند (آرایه)، تک‌مقداری مثل قبل.
                if (Object.prototype.hasOwnProperty.call(values, el.name)) {
                    if (!Array.isArray(values[el.name])) values[el.name] = [values[el.name]];
                    values[el.name].push(el.value);
                } else {
                    values[el.name] = el.value;
                }
            });
            root.dispatchEvent(new CustomEvent("ak:filter-change", {
                bubbles: true,
                detail: { search: input.value || "", filters: values }
            }));
        }

        function closePopover() {
            if (!popover) return;
            // A date panel inside the popover portals its calendar to the body-level
            // overlay; close it first so clearing the popover cannot orphan it.
            popover.querySelectorAll('.ak-datepicker[data-open="true"]').forEach(function (dp) {
                if (typeof dp._akDateClose === "function") dp._akDateClose();
            });
            popover.hidden = true;
            popover.innerHTML = "";
            nav.state = "field";
            nav.field = null;
        }

        // ---- Popover rendering ----
        function openFieldList() {
            nav.state = "field"; nav.field = null;
            renderPopover();
        }

        function renderPopover() {
            if (!popover) return;
            var q = input.value.trim();

            if (!hasFilters) {
                // group A: plain search hint
                if (!q) return closePopover();
                popover.innerHTML =
                    '<button type="submit" class="ak-option ak-option-search-btn" data-ak-search-text>' +
                    T("جستجو برای «", "Search for “") + esc(q) + T("»", "”") + "</button>";
                popover.hidden = false;
                return;
            }

            var html = "";
            if (nav.state === "value" && nav.field) {
                html = renderValue(nav.field);
            } else if (nav.state === "date" && nav.field) {
                html = renderDate(nav.field);
            } else if (nav.state === "text" && nav.field) {
                html = renderText(nav.field);
            } else {
                html = renderFields(q);
            }
            popover.innerHTML = html;
            popover.hidden = !html;
        }

        function renderFields(q) {
            var avail = availableFields(q);
            var rows = avail.map(function (f) {
                var count = f.multiple ? appliedValues(f.key).length : 0;
                var meta = count ? T(count + " مورد", count + " selected") : fieldTypeLabel(f.type);
                return '<li><button type="button" class="ak-option" data-ak-field="' + esc(f.key) + '">' +
                    '<span>' + esc(f.label) + "</span>" +
                    '<span class="ak-option-meta">' + esc(meta) + "</span></button></li>";
            }).join("");
            var searchRow = q
                ? '<li class="ak-option-search"><button type="submit" class="ak-option ak-option-search-btn" data-ak-search-text>' +
                    T("جستجو برای «", "Search for “") + esc(q) + T("»", "”") + "</button></li>"
                : "";
            if (!rows && !searchRow) {
                return '<li class="ak-option" aria-disabled="true">' + T("فیلتری باقی نمانده", "No filters left") + "</li>";
            }
            return '<ul class="ak-option-list">' + rows + searchRow + "</ul>";
        }

        function fieldTypeLabel(type) {
            if (type === "date" || type === "daterange") return T("تاریخ", "date");
            if (type === "bool") return T("وضعیت", "status");
            if (type === "text") return T("متن", "text");
            return T("انتخاب", "select");
        }

        function renderMultiValue(f) {
            var head = popHead(f.label);
            var selected = appliedValues(f.key);
            var options = f.options || [];
            var rows = options.map(function (o) {
                var on = selected.indexOf(String(o.value)) !== -1;
                return '<li><label class="ak-option ak-option-check' + (on ? " is-checked" : "") +
                    '" data-ak-multi-row data-ak-option-label="' + esc(String(o.label).toLocaleLowerCase()) + '">' +
                    '<input type="checkbox" class="ak-check" data-ak-multi-value value="' + esc(o.value) + '"' +
                    (on ? " checked" : "") + '>' +
                    '<span>' + esc(o.label) + "</span></label></li>";
            }).join("");
            if (!rows) rows = '<li class="ak-option" aria-disabled="true">' + T("مقداری نیست", "No values") + "</li>";
            var search = options.length > 8
                ? '<div class="ak-multi-search"><input type="text" class="ak-text-input" data-ak-value-search' +
                    ' autocomplete="off" placeholder="' + T("جستجو در مقادیر…", "Search values…") + '"></div>'
                : "";
            return head + search +
                '<ul class="ak-option-list ak-option-list--multi" data-ak-multi-list>' + rows + "</ul>" +
                '<div class="ak-multi-foot">' +
                    '<button type="button" class="ak-multi-clear" data-ak-multi-clear>' +
                        T("پاک کردن", "Clear") + "</button>" +
                    '<button type="button" class="ak-fpop-apply" data-ak-multi-apply>' +
                        T("اعمال", "Apply") + "</button>" +
                "</div>";
        }

        function renderValue(f) {
            if (f.multiple) return renderMultiValue(f);
            var head = popHead(f.label);
            var rows = (f.options || []).map(function (o) {
                return '<li><button type="button" class="ak-option" data-ak-value="' + esc(o.value) + '"' +
                    ' data-ak-value-label="' + esc(o.label) + '"><span>' + esc(o.label) + "</span></button></li>";
            }).join("");
            if (!rows) rows = '<li class="ak-option" aria-disabled="true">' + T("مقداری نیست", "No values") + "</li>";
            return head + '<ul class="ak-option-list">' + rows + "</ul>";
        }

        function renderDate(f) {
            var head = popHead(f.label);
            var range = f.type === "daterange";
            return head +
                '<div class="ak-date-panel">' +
                    "<label>" + (range ? T("از تاریخ", "Start date") : T("تاریخ", "Date")) +
                        '<input type="date" data-ak-date-start></label>' +
                    (range ? "<label>" + T("تا تاریخ", "End date") + '<input type="date" data-ak-date-end></label>' : "") +
                    '<button type="button" class="ak-fpop-apply ak-date-apply" data-ak-date-apply>' +
                        T("اعمال", "Apply") + "</button>" +
                "</div>";
        }

        function renderText(f) {
            var head = popHead(f.label);
            return head +
                '<div class="ak-text-panel">' +
                    '<input type="text" class="ak-text-input" data-ak-text-input autocomplete="off" placeholder="' +
                        esc(f.label) + '">' +
                    '<button type="button" class="ak-fpop-apply ak-text-apply" data-ak-text-apply>' +
                        T("اعمال", "Apply") + "</button>" +
                "</div>";
        }

        function popHead(label) {
            return '<div class="ak-pop-head"><button type="button" class="ak-pop-back" data-ak-back aria-label="' +
                T("بازگشت", "Back") + '">‹</button><span>' + esc(label) + "</span></div>";
        }

        // ---- Commit / remove (mutate hidden inputs, then submit) ----
        function cssEsc(s) { return String(s).replace(/["\\]/g, "\\$&"); }

        function addHidden(name, ownerKey, value) {
            var el = document.createElement("input");
            el.type = "hidden";
            el.name = name;
            el.value = value;
            el.setAttribute("data-ak-filter-param", ownerKey);
            stateHost.appendChild(el);
        }

        function commitValue(f, value) {
            // one token per field: clear prior inputs owned by this key
            Array.prototype.forEach.call(stateHost.querySelectorAll('[data-ak-filter-param="' + cssEsc(f.key) + '"]'),
                function (el) { el.remove(); });
            addHidden(f.key, f.key, value);
            input.value = "";           // filters submit without free-text noise from the draft
            applyChanges();
        }

        function commitMulti(f) {
            var values = Array.prototype.slice.call(popover.querySelectorAll("[data-ak-multi-value]"))
                .filter(function (el) { return el.checked; })
                .map(function (el) { return el.value; });
            ownedInputs(f.key).forEach(function (el) { el.remove(); });
            // یک input پنهان برای هر مقدار، همگی با یک نام: OR داخل همین فیلتر.
            values.forEach(function (v) { addHidden(f.key, f.key, v); });
            input.value = "";
            applyChanges();
        }

        function commitDate(f) {
            var start = popover.querySelector("[data-ak-date-start]");
            var end = popover.querySelector("[data-ak-date-end]");
            var s = start ? start.value : "";
            if (!s) return;
            Array.prototype.forEach.call(stateHost.querySelectorAll('[data-ak-filter-param="' + cssEsc(f.key) + '"]'),
                function (el) { el.remove(); });
            addHidden(f.key, f.key, s);
            if (f.type === "daterange" && f.secondKey) {
                var e = end && end.value ? end.value : s;
                addHidden(f.secondKey, f.key, e);
            }
            applyChanges();
        }

        function commitText(f) {
            var el = popover.querySelector("[data-ak-text-input]");
            var v = el ? el.value.trim() : "";
            if (!v) return;
            Array.prototype.forEach.call(stateHost.querySelectorAll('[data-ak-filter-param="' + cssEsc(f.key) + '"]'),
                function (x) { x.remove(); });
            addHidden(f.key, f.key, v);
            input.value = "";
            applyChanges();
        }

        function removeFilter(key) {
            Array.prototype.forEach.call(stateHost.querySelectorAll('[data-ak-filter-param="' + cssEsc(key) + '"]'),
                function (el) { el.remove(); });
            var chip = root.querySelector('[data-ak-chip="' + cssEsc(key) + '"]');
            if (chip) chip.remove();
            applyChanges();
        }

        function selectField(key) {
            var f = fields.filter(function (x) { return x.key === key; })[0];
            if (!f) return;
            nav.field = f;
            if (f.type === "date" || f.type === "daterange") nav.state = "date";
            else if (f.type === "text") nav.state = "text";
            else nav.state = "value";
            renderPopover();
        }

        // ---- Events ----
        input.addEventListener("focus", function () {
            root.classList.add("is-focused");
            if (hasFilters) openFieldList();
            else renderPopover();
        });
        input.addEventListener("input", function () {
            syncClear();
            if (clientMode) applyChanges();
            if (nav.state === "value" || nav.state === "date") { renderPopover(); return; }
            renderPopover();
        });
        input.addEventListener("keydown", function (e) {
            if (e.key === "Escape") closePopover();
            if (clientMode && e.key === "Enter") {
                e.preventDefault();
                applyChanges();
                closePopover();
            }
        });
        if (popover) {
            popover.addEventListener("input", function (e) {
                var box = e.target.closest("[data-ak-value-search]");
                if (!box) return;
                var q = box.value.trim().toLocaleLowerCase();
                popover.querySelectorAll("[data-ak-multi-row]").forEach(function (row) {
                    var label = row.getAttribute("data-ak-option-label") || "";
                    row.closest("li").hidden = !!q && label.indexOf(q) === -1;
                });
            });
            popover.addEventListener("keydown", function (e) {
                if (e.key === "Escape") { closePopover(); return; }
                if (e.key === "Enter" && nav.field && nav.field.multiple &&
                    e.target.closest("[data-ak-value-search], [data-ak-multi-list]")) {
                    e.preventDefault();
                    commitMulti(nav.field);
                    return;
                }
                if (e.key === "Enter" && e.target.closest("[data-ak-text-input]") && nav.field) {
                    e.preventDefault();
                    commitText(nav.field);
                }
            });
        }

        root.addEventListener("click", function (e) {
            var enter = e.target.closest("[data-ak-filter-enter], [data-ak-search-text]");
            if (clientMode && enter) {
                e.preventDefault();
                applyChanges();
                closePopover();
                return;
            }

            var back = e.target.closest("[data-ak-back]");
            if (back) { e.preventDefault(); openFieldList(); return; }

            var fieldBtn = e.target.closest("[data-ak-field]");
            if (fieldBtn) { e.preventDefault(); selectField(fieldBtn.getAttribute("data-ak-field")); return; }

            var valueBtn = e.target.closest("[data-ak-value]");
            if (valueBtn && nav.field) { e.preventDefault(); commitValue(nav.field, valueBtn.getAttribute("data-ak-value")); return; }

            var multiApply = e.target.closest("[data-ak-multi-apply]");
            if (multiApply && nav.field) { e.preventDefault(); commitMulti(nav.field); return; }

            var multiClear = e.target.closest("[data-ak-multi-clear]");
            if (multiClear && nav.field) {
                e.preventDefault();
                popover.querySelectorAll("[data-ak-multi-value]").forEach(function (el) {
                    el.checked = false;
                    var row = el.closest("[data-ak-multi-row]");
                    if (row) row.classList.remove("is-checked");
                });
                return;
            }

            var checkRow = e.target.closest("[data-ak-multi-row]");
            if (checkRow) {
                // کلیک روی ردیف = تغییر وضعیت تیک؛ popover باز می‌ماند تا چند مورد انتخاب شود.
                var box = checkRow.querySelector("[data-ak-multi-value]");
                if (box) {
                    if (e.target !== box) { e.preventDefault(); box.checked = !box.checked; }
                    checkRow.classList.toggle("is-checked", box.checked);
                }
                return;
            }

            var editChip = e.target.closest("[data-ak-edit-chip]");
            if (editChip) {
                e.preventDefault();
                selectField(editChip.getAttribute("data-ak-edit-chip"));
                return;
            }

            var dateApply = e.target.closest("[data-ak-date-apply]");
            if (dateApply && nav.field) { e.preventDefault(); commitDate(nav.field); return; }

            var textApply = e.target.closest("[data-ak-text-apply]");
            if (textApply && nav.field) { e.preventDefault(); commitText(nav.field); return; }

            var removeBtn = e.target.closest("[data-ak-remove-chip]");
            if (removeBtn) { e.preventDefault(); removeFilter(removeBtn.getAttribute("data-ak-remove-chip")); return; }
        });

        if (clearBtn) {
            clearBtn.addEventListener("click", function () {
                if (!(input.value.trim() || hasActiveFilters())) return;
                input.value = "";
                Array.prototype.forEach.call(stateHost.querySelectorAll("[data-ak-filter-param]"),
                    function (el) { el.remove(); });
                closePopover();
                applyChanges();
            });
        }

        root._akClose = closePopover;
        syncClientChips();
        syncClear();
    }

    // Single global outside-click handler (no per-mount listener leak on SPA nav).
    var globalBound = false;
    function bindGlobal() {
        if (globalBound) return;
        globalBound = true;
        document.addEventListener("pointerdown", function (e) {
            // The portalled date calendar lives in .ak-overlay-root, outside the
            // filter root — clicking a day must not count as an outside click.
            if (e.target.closest && e.target.closest(".ak-overlay-root")) return;
            document.querySelectorAll("[data-ak-filter]").forEach(function (root) {
                if (!root.contains(e.target)) {
                    if (typeof root._akClose === "function") root._akClose();
                    root.classList.remove("is-focused");
                }
            });
        }, true);
    }

    function init() {
        document.querySelectorAll("[data-ak-filter]").forEach(mount);
        bindGlobal();
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init, { once: true });
    } else {
        init();
    }
    window.addEventListener("ptg:page-ready", init);
})();
