(function () {
    "use strict";

    var svgNs = "http://www.w3.org/2000/svg";

    function parseJson(container, name, fallback) {
        try {
            var value = JSON.parse(container.dataset[name] || fallback);
            return value;
        } catch (_error) {
            return JSON.parse(fallback);
        }
    }

    function parseList(container, name) {
        var value = parseJson(container, name, "[]");
        return Array.isArray(value) ? value : [];
    }

    function number(value) {
        var parsed = Number(value);
        return Number.isFinite(parsed) ? parsed : 0;
    }

    function svgElement(name, attributes) {
        var element = document.createElementNS(svgNs, name);
        Object.keys(attributes || {}).forEach(function (key) {
            element.setAttribute(key, attributes[key]);
        });
        return element;
    }

    function locale() {
        return document.documentElement.lang === "en" ? "en-US" : "fa-AF";
    }

    function formatCompact(value) {
        return new Intl.NumberFormat(locale(), {
            notation: "compact",
            maximumFractionDigits: 1
        }).format(value);
    }

    function appendText(parent, className, value, x, y) {
        var text = svgElement("text", { x: x, y: y, class: className });
        text.textContent = value;
        parent.appendChild(text);
    }

    // ===== area / line trend chart =====
    function linePath(points) {
        if (!points.length) return "";
        var path = "M " + points[0].x + " " + points[0].y;
        for (var index = 1; index < points.length; index += 1) {
            var previous = points[index - 1];
            var current = points[index];
            var middle = (previous.x + current.x) / 2;
            path += " C " + middle + " " + previous.y + ", " + middle + " " + current.y + ", " + current.x + " " + current.y;
        }
        return path;
    }

    function areaPath(points, baseline) {
        if (!points.length) return "";
        return linePath(points) + " L " + points[points.length - 1].x + " " + baseline + " L " + points[0].x + " " + baseline + " Z";
    }

    function seriesPoints(values, maximum, count, left, top, plotWidth, plotHeight) {
        return values.map(function (value, index) {
            var x = count <= 1 ? left + (plotWidth / 2) : left + ((plotWidth * index) / (count - 1));
            var y = top + plotHeight - ((Math.max(0, value) / maximum) * plotHeight);
            return { x: x, y: y, value: value };
        });
    }

    function appendGradients(svg) {
        var defs = svgElement("defs", {});
        [["dashAreaGreen", "34, 197, 94"], ["dashAreaAmber", "245, 165, 36"]].forEach(function (pair) {
            var grad = svgElement("linearGradient", {
                id: pair[0], x1: "0", y1: "0", x2: "0", y2: "1"
            });
            grad.appendChild(svgElement("stop", { offset: "0", "stop-color": "rgb(" + pair[1] + ")", "stop-opacity": "0.30" }));
            grad.appendChild(svgElement("stop", { offset: "1", "stop-color": "rgb(" + pair[1] + ")", "stop-opacity": "0" }));
            defs.appendChild(grad);
        });
        svg.appendChild(defs);
    }

    function appendSeries(svg, points, kind, baseline) {
        svg.appendChild(svgElement("path", {
            d: areaPath(points, baseline),
            class: "dash-chart-area dash-chart-area--" + kind,
            "aria-hidden": "true"
        }));
        svg.appendChild(svgElement("path", {
            d: linePath(points),
            class: "dash-chart-line dash-chart-line--" + kind
        }));
    }

    function renderEmpty(container) {
        var empty = document.createElement("div");
        var icon = document.createElement("i");
        var label = document.createElement("span");
        empty.className = "dash-chart-empty";
        icon.className = "bi bi-graph-up-arrow";
        icon.setAttribute("aria-hidden", "true");
        label.textContent = container.dataset.emptyLabel || "No data";
        empty.append(icon, label);
        container.appendChild(empty);
    }

    function renderChart(container) {
        if (!container) return;

        var labels = parseList(container, "labels").map(String);
        var sales = parseList(container, "sales").map(number);
        var expenses = parseList(container, "expenses").map(number);
        var count = Math.min(labels.length, sales.length, expenses.length);
        var maximum = Math.max.apply(null, sales.concat(expenses, [0]));
        container.replaceChildren();

        if (count === 0 || maximum <= 0) {
            renderEmpty(container);
            return;
        }

        labels = labels.slice(0, count);
        sales = sales.slice(0, count);
        expenses = expenses.slice(0, count);

        var width = 780;
        var height = 300;
        var left = 46;
        var right = 14;
        var top = 16;
        var bottom = 40;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var baseline = top + plotHeight;

        var svg = svgElement("svg", {
            viewBox: "0 0 " + width + " " + height,
            preserveAspectRatio: "none",
            role: "img",
            focusable: "false",
            "aria-label": container.dataset.chartLabel || "Trend chart"
        });
        appendGradients(svg);

        for (var gridIndex = 0; gridIndex <= 5; gridIndex += 1) {
            var ratio = gridIndex / 5;
            var y = top + (plotHeight * ratio);
            svg.appendChild(svgElement("line", {
                x1: left, y1: y, x2: left + plotWidth, y2: y,
                class: "dash-chart-grid", "aria-hidden": "true"
            }));
            appendText(svg, "dash-chart-axis-label", formatCompact(maximum * (1 - ratio)), left - 8, y + 4);
        }

        var labelIndexes = [];
        for (var t = 0; t < count; t += 1) {
            if (count <= 7 || t % Math.ceil(count / 7) === 0 || t === count - 1) {
                labelIndexes.push(t);
            }
        }
        labelIndexes.forEach(function (index) {
            var x = count <= 1 ? left + (plotWidth / 2) : left + ((plotWidth * index) / (count - 1));
            appendText(svg, "dash-chart-x-label", labels[index], x, height - 12);
        });

        var salesPoints = seriesPoints(sales, maximum, count, left, top, plotWidth, plotHeight);
        var expensePoints = seriesPoints(expenses, maximum, count, left, top, plotWidth, plotHeight);
        appendSeries(svg, expensePoints, "expenses", baseline);
        appendSeries(svg, salesPoints, "sales", baseline);

        container.appendChild(svg);
    }

    // ===== sales-mix concentric donut =====
    function renderDonut(container) {
        if (!container) return;

        var segments = parseJson(container, "segments", "[]");
        if (!Array.isArray(segments)) segments = [];
        var total = number(container.dataset.total);
        if (total <= 0) {
            total = segments.reduce(function (sum, s) { return sum + number(s.value); }, 0);
        }
        container.replaceChildren();

        var size = 200;
        var center = size / 2;
        var svg = svgElement("svg", {
            viewBox: "0 0 " + size + " " + size,
            role: "img",
            focusable: "false",
            "aria-label": (container.dataset.totalLabel || "Total") + " " + total
        });

        var radii = [82, 62, 42];
        var strokeWidth = 13;

        segments.slice(0, radii.length).forEach(function (seg, index) {
            var radius = radii[index];
            var circumference = 2 * Math.PI * radius;
            var fraction = total > 0 ? Math.min(1, number(seg.value) / total) : 0;

            // track
            svg.appendChild(svgElement("circle", {
                cx: center, cy: center, r: radius,
                fill: "none", stroke: "#eef1f6", "stroke-width": strokeWidth,
                "aria-hidden": "true"
            }));
            // progress
            var arc = svgElement("circle", {
                cx: center, cy: center, r: radius,
                fill: "none",
                stroke: seg.color || "#22c55e",
                "stroke-width": strokeWidth,
                "stroke-linecap": "round",
                "stroke-dasharray": (fraction * circumference) + " " + circumference,
                transform: "rotate(-90 " + center + " " + center + ")"
            });
            var title = svgElement("title");
            title.textContent = (seg.label || "") + " · " + number(seg.value);
            arc.appendChild(title);
            svg.appendChild(arc);
        });

        appendText(svg, "dash-donut-center-label", container.dataset.totalLabel || "Total", center, center - 8);
        appendText(svg, "dash-donut-center-total",
            new Intl.NumberFormat(locale()).format(total), center, center + 26);

        container.appendChild(svg);
    }

    function renderDashboardCharts() {
        document.querySelectorAll("[data-dashboard-chart]").forEach(renderChart);
        document.querySelectorAll("[data-dashboard-donut]").forEach(renderDonut);
    }

    window.__ptgRenderDashboardCharts = renderDashboardCharts;
    if (window.__ptgDashboardChartsReady !== true) {
        window.addEventListener("ptg:page-ready", renderDashboardCharts);
        window.addEventListener("pageshow", renderDashboardCharts);
        window.__ptgDashboardChartsReady = true;
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", renderDashboardCharts, { once: true });
    } else {
        renderDashboardCharts();
    }
}());
