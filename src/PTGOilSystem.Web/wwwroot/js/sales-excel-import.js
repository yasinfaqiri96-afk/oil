(() => {
    const form = document.getElementById('sales-import-mappings');
    if (!form) return;
    const invalidate = () => document.getElementById('sales-import-final')?.remove();
    form.addEventListener('change', invalidate);
    form.addEventListener('input', invalidate);
    form.addEventListener('click', event => {
        if (event.target.closest('[data-copy-source]')) {
            const source = document.getElementById('import-default-source').value;
            const terminal = document.getElementById('import-default-terminal').value;
            if (!source) return;
            for (const select of form.querySelectorAll('select[name$=".SourceKey"]')) {
                if (select.value) continue;
                select.value = source;
                if (source.startsWith('Loading:') && terminal) {
                    select.closest('td').querySelector('select[name$=".SaleTerminalId"]').value = terminal;
                }
            }
            invalidate();
            return;
        }
        const button = event.target.closest('[data-copy-buyer]');
        if (!button) return;
        const source = button.parentElement.querySelector('select[data-buyer-name]');
        if (!source.value) return;
        for (const select of form.querySelectorAll('select[data-buyer-name]')) {
            if (select.dataset.buyerName === source.dataset.buyerName) select.value = source.value;
        }
        invalidate();
    });
})();
