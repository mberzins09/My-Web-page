window.setupNavMenuEscapeKey = (toggleRef) => {
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
            const toggler = document.querySelector('.navbar-toggler');
            if (toggler && toggler.checked) {
                toggler.checked = false;
            }
        }
    });
};

window.closeNavMenu = (toggleRef) => {
    const toggler = document.querySelector('.navbar-toggler');
    if (toggler) {
        toggler.checked = false;
    }
};

// Only re-check the checkbox if we are actually on mobile.
// On desktop this is a no-op so the backdrop never appears.
window.openNavMenuIfMobile = () => {
    if (window.innerWidth <= 640) {
        const toggler = document.querySelector('.navbar-toggler');
        if (toggler) {
            toggler.checked = true;
        }
    }
};
