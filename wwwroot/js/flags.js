// Windows (Chrome/Edge/Firefox) doesn't render flag emoji as pictures - it shows the two letters
// as boxed tiles instead. Twemoji fixes this by swapping a flag character for a small image, so
// flags look the same on every OS/browser.
//
// IMPORTANT: only flag sequences are touched. Twemoji's own callback below rejects every other
// emoji (checkmarks, icons in buttons/headers, etc.) so they stay exactly as they were - the first
// version of this script replaced ALL emoji on the page, which is what made everything huge.
//
// A MutationObserver re-applies this to new content automatically, since Blazor Server updates the
// page by patching the DOM over SignalR rather than doing full page loads.
(function () {
    // A flag is exactly two Regional Indicator Symbols (U+1F1E6-U+1F1FF) joined by twemoji as
    // "xxxx-yyyy" hex codepoints - e.g. Latvia (🇱🇻) is "1f1f1-1f1fb". Anything else (a single
    // codepoint, or codepoints outside that range) is a normal emoji and is left untouched.
    function isFlagIcon(icon) {
        var parts = icon.split('-');
        if (parts.length !== 2) return false;
        return parts.every(function (p) {
            var n = parseInt(p, 16);
            return n >= 0x1F1E6 && n <= 0x1F1FF;
        });
    }

    function applyTwemoji(node) {
        if (window.twemoji && node) {
            window.twemoji.parse(node, {
                folder: 'svg',
                ext: '.svg',
                className: 'flag-emoji',
                callback: function (icon, options) {
                    if (!isFlagIcon(icon)) return false;   // leave every non-flag emoji as plain text
                    // Build the src ourselves from what WE passed above, rather than trusting how
                    // twemoji echoes folder/ext back on `options` (varies by version) - this is the
                    // same jsDelivr layout twemoji's own default folder-mode callback produces.
                    return options.base + 'svg/' + icon + '.svg';
                }
            });
        }
    }

    function start() {
        applyTwemoji(document.body);

        var observer = new MutationObserver(function (mutations) {
            mutations.forEach(function (m) {
                if (m.type === 'childList') {
                    m.addedNodes.forEach(function (n) {
                        if (n.nodeType === 1) applyTwemoji(n);
                        else if (n.nodeType === 3 && n.parentNode) applyTwemoji(n.parentNode);
                    });
                } else if (m.type === 'characterData' && m.target.parentNode) {
                    applyTwemoji(m.target.parentNode);
                }
            });
        });

        observer.observe(document.body, { childList: true, subtree: true, characterData: true });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
