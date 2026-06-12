// Auto-select input content on focus for number and date fields.
// Works for all current and future inputs — no per-input changes needed.
document.addEventListener('focus', function (e) {
    if (e.target.matches('input[type="number"], input[type="date"], input[type="datetime-local"]')) {
        e.target.select();
    }
}, true); // `true` = capture phase, fires before Blazor's own handlers