// This asset is emitted only by a truck-only client profile; no domain rules change.
(() => {
    let scheduled = false;
    const apply = () => {
        scheduled = false;
        document.querySelectorAll('select[name*="TransportType"]').forEach(select => {
            let changed = false;
            Array.from(select.options).forEach(option => {
                if (['1', '2'].includes(option.value)) {
                    if (option.selected) changed = true;
                    option.remove();
                }
            });
            if ((changed || select.value === '0' || select.value === '') &&
                Array.from(select.options).some(option => option.value === '3')) {
                select.value = '3';
                select.dispatchEvent(new Event('change', { bubbles: true }));
            }
        });
    };
    const schedule = () => {
        if (!scheduled) {
            scheduled = true;
            requestAnimationFrame(apply);
        }
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', apply);
    else apply();
    new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
})();
