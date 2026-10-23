/*
 * PTG Oil System — list grid (Mashal reference 5).
 * Adds the reference list chrome to canonical Index tables: a "#" row index;
 * local per-column filtering and sorting are limited to fully rendered tables.
 * Presentation only: it works on the rows already rendered on the page; server
 * search, pagination, export, row navigation and bulk selection are untouched.
 */
(function () {
    "use strict";

    var isEn = (document.documentElement.getAttribute("lang") || "fa").toLowerCase().indexOf("en") === 0;
    function T(fa, en) { return isEn ? en : fa; }

    var skipColumns = ".ak-col-check, .ak-col-index, .ak-col-actions, .ak-col-spacer";

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", function () { window.setTimeout(init, 0); }, { once: true });
    } else {
        window.setTimeout(init, 0);
    }

    // tables.js re-initialises on the next frame; run after it so avatars, row
    // actions and the client pager already exist.
    window.addEventListener("ptg:page-ready", function () {
        window.requestAnimationFrame(function () { window.requestAnimationFrame(init); });
    });

    function init() {
        if (!document.body.classList.contains("action-index")) return;
        document.querySelectorAll("#ptg-main-content table.ak-table").forEach(setup);
    }

    function setup(table) {
        if (table.dataset.listGridReady === "true") return;
        if (table.closest("[data-ak-detail-v2='true'], [data-list-grid='off'], .modal")) return;

        var head = table.tHead;
        var body = table.tBodies && table.tBodies[0];
        if (!head || !body || head.rows.length !== 1) return;

        var headRow = head.rows[0];
        var headCells = Array.prototype.slice.call(headRow.cells);
        if (!headCells.length || headCells.some(function (c) { return c.colSpan > 1 || c.rowSpan > 1; })) return;

        table.dataset.listGridReady = "true";
        var originalCount = headCells.length;
        var checkIndex = headCells.findIndex(function (c) { return c.classList.contains("ak-col-check"); });
        var insertAt = checkIndex + 1;

        var indexHead = document.createElement("th");
        indexHead.className = "ak-col-index";
        indexHead.scope = "col";
        indexHead.textContent = "#";
        headRow.insertBefore(indexHead, headRow.cells[insertAt] || null);
        shiftRows(table, insertAt, originalCount);

        var columnCount = headRow.cells.length;
        var filters = [];
        var filterRow = document.createElement("tr");
        filterRow.className = "ak-filter-row";
        var partialPage = hasMorePages(table);

        Array.prototype.forEach.call(headRow.cells, function (cell, index) {
            var slot = document.createElement("td");
            slot.className = "ak-filter-cell";
            if (isDataColumn(cell) && !partialPage) {
                var input = document.createElement("input");
                input.type = "search";
                input.className = "ak-col-filter";
                input.autocomplete = "off";
                input.size = 1;
                input.placeholder = T("فیلتر و جستجو", "Filter and search");
                input.setAttribute("aria-label", T("فیلتر ستون ", "Filter column ") + label(cell));
                input.addEventListener("input", debounce(applyFilters, 150));
                slot.appendChild(input);
                filters.push({ index: index, input: input });
                makeSortable(cell, index);
            }
            filterRow.appendChild(slot);
        });
        if (!partialPage) head.appendChild(filterRow);

        var emptyRow = null;
        var sortState = { index: -1, dir: 0 };
        dataRows().forEach(function (row, order) { row.dataset.listGridOrder = String(order); });
        number();

        function dataRows() {
            return Array.prototype.filter.call(body.rows, function (row) {
                return row.cells.length === columnCount && !row.classList.contains("ak-table-total")
                    && !row.hasAttribute("data-contract-breakdown-row") && row !== emptyRow;
            });
        }

        function applyFilters() {
            var active = filters
                .map(function (f) { return { index: f.index, value: normalize(f.input.value) }; })
                .filter(function (f) { return f.value; });
            var shown = 0;
            dataRows().forEach(function (row) {
                var match = active.every(function (f) { return normalize(row.cells[f.index].textContent).indexOf(f.value) !== -1; });
                row.hidden = !match;
                if (match) { delete row.dataset.listGridHidden; shown += 1; }
                else { row.dataset.listGridHidden = "true"; collapseBreakdown(row); }
            });
            toggleEmpty(active.length > 0 && shown === 0);
            refreshPager();
            number();
        }

        function makeSortable(cell, index) {
            cell.classList.add("ak-sortable");
            cell.setAttribute("aria-sort", "none");
            cell.tabIndex = 0;
            var icon = document.createElement("span");
            icon.className = "ak-sort-ic";
            icon.setAttribute("aria-hidden", "true");
            cell.insertBefore(icon, cell.firstChild);
            function toggle() {
                sortState.dir = sortState.index === index ? (sortState.dir + 1) % 3 : 1;
                sortState.index = sortState.dir ? index : -1;
                Array.prototype.forEach.call(headRow.cells, function (h) {
                    if (h.classList.contains("ak-sortable")) h.setAttribute("aria-sort", "none");
                });
                if (sortState.dir) cell.setAttribute("aria-sort", sortState.dir === 1 ? "ascending" : "descending");
                sortRows();
            }
            cell.addEventListener("click", function (event) {
                if (event.target.closest("a, button, input, label")) return;
                toggle();
            });
            cell.addEventListener("keydown", function (event) {
                if (event.key === "Enter" || event.key === " ") { event.preventDefault(); toggle(); }
            });
        }

        function sortRows() {
            var rows = dataRows();
            var index = sortState.index;
            var dir = sortState.dir === 2 ? -1 : 1;
            rows.sort(function (a, b) {
                if (index < 0) return Number(a.dataset.listGridOrder) - Number(b.dataset.listGridOrder);
                return compare(a.cells[index].textContent, b.cells[index].textContent) * dir
                    || Number(a.dataset.listGridOrder) - Number(b.dataset.listGridOrder);
            });
            var trailing = Array.prototype.filter.call(body.rows, function (row) {
                return rows.indexOf(row) === -1 && !row.hasAttribute("data-contract-breakdown-row");
            });
            rows.forEach(function (row) {
                body.appendChild(row);
                var key = row.dataset.breakdownParent;
                var detail = key ? body.querySelector("[data-contract-breakdown-row='" + key + "']") : null;
                if (detail) body.appendChild(detail);
            });
            trailing.forEach(function (row) { body.appendChild(row); });
            refreshPager();
            number();
        }

        function number() {
            var offset = pageOffset(table);
            var n = 0;
            dataRows().forEach(function (row) {
                var cell = row.cells[insertAt];
                if (!cell || !cell.classList.contains("ak-col-index")) return;
                if (row.dataset.listGridHidden === "true") { cell.textContent = ""; return; }
                n += 1;
                cell.textContent = String(offset + n);
            });
        }

        function toggleEmpty(show) {
            if (show && !emptyRow) {
                emptyRow = document.createElement("tr");
                emptyRow.className = "ak-filter-empty";
                var cell = document.createElement("td");
                cell.colSpan = columnCount;
                cell.textContent = T("موردی با این فیلتر یافت نشد.", "No rows match this filter.");
                emptyRow.appendChild(cell);
                body.appendChild(emptyRow);
            } else if (!show && emptyRow) {
                emptyRow.remove();
                emptyRow = null;
            }
        }

        function refreshPager() {
            if (typeof table.ptgClientPagerRefresh === "function") table.ptgClientPagerRefresh();
        }

        function collapseBreakdown(row) {
            var key = row.dataset.breakdownParent;
            var detail = key ? body.querySelector("[data-contract-breakdown-row='" + key + "']") : null;
            if (detail) detail.hidden = true;
        }
    }

    // Keep every non-header row aligned with the new "#" column: plain rows get
    // an index cell, merged rows (empty state, totals, breakdowns) grow by one.
    function shiftRows(table, insertAt, originalCount) {
        var sections = Array.prototype.slice.call(table.tBodies);
        if (table.tFoot) sections.push(table.tFoot);
        sections.forEach(function (section) {
            Array.prototype.forEach.call(section.rows, function (row) {
                if (row.cells.length === originalCount && !Array.prototype.some.call(row.cells, function (c) { return c.colSpan > 1; })) {
                    var cell = document.createElement("td");
                    cell.className = "ak-col-index";
                    row.insertBefore(cell, row.cells[insertAt] || null);
                    return;
                }
                var position = 0;
                for (var i = 0; i < row.cells.length; i += 1) {
                    position += row.cells[i].colSpan;
                    if (position > insertAt || i === row.cells.length - 1) { row.cells[i].colSpan += 1; return; }
                }
            });
        });
    }

    function isDataColumn(cell) {
        return !cell.matches(skipColumns) && !cell.querySelector("input, .ak-check") && label(cell) !== "";
    }

    function label(cell) {
        return String(cell.textContent || "").replace(/\s+/g, " ").trim();
    }

    function hasMorePages(table) {
        var list = table.closest(".ak-list, .ak-list-page, .ak-form-page");
        return !!(list && list.querySelector("[data-server-pagination='true'] .pagination"));
    }

    function pageOffset(table) {
        var params = new URLSearchParams(window.location.search);
        var page = parseInt(params.get("page") || "1", 10);
        if (!(page > 1)) return 0;
        var size = parseInt(params.get("pageSize") || "", 10);
        if (!(size > 0)) {
            var list = table.closest(".ak-list, .ak-list-page, .ak-form-page");
            var select = list && list.querySelector(".ak-page-size-select");
            var option = select && select.options[select.selectedIndex];
            size = option ? parseInt(toLatin(option.textContent), 10) : 0;
        }
        return size > 0 ? (page - 1) * size : 0;
    }

    function toLatin(value) {
        return String(value || "")
            .replace(/[۰-۹]/g, function (d) { return String(d.charCodeAt(0) - 0x06F0); })
            .replace(/[٠-٩]/g, function (d) { return String(d.charCodeAt(0) - 0x0660); });
    }

    function normalize(value) {
        return toLatin(value).replace(/ي/g, "ی").replace(/ك/g, "ک").replace(/‌/g, " ")
            .replace(/\s+/g, " ").trim().toLowerCase();
    }

    function numeric(value) {
        var text = toLatin(value).replace(/[,٬]/g, "");
        if (!/^\s*-?\d+(\.\d+)?\s*[A-Za-z%؀-ۿ]*\s*$/.test(text)) return null;
        return parseFloat(text);
    }

    function compare(a, b) {
        var x = numeric(a);
        var y = numeric(b);
        if (x !== null && y !== null) return x - y;
        return normalize(a).localeCompare(normalize(b), isEn ? "en" : "fa", { numeric: true });
    }

    function debounce(fn, wait) {
        var timer;
        return function () {
            window.clearTimeout(timer);
            timer = window.setTimeout(fn, wait);
        };
    }
})();
