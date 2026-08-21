// Opens and closes the touch navigation fan.
//
// The arc itself is CSS. This only holds the open state and keeps the parts that
// cannot be expressed in a stylesheet honest: what a screen reader is told, what the
// keyboard can reach, and what the back button does.
(function () {
    var fan = document.getElementById('fwFan');
    if (!fan) return;

    var toggle   = document.getElementById('fwFanToggle');
    var list     = document.getElementById('fwFanList');
    var backdrop = document.getElementById('fwFanBackdrop');
    var links    = list ? list.querySelectorAll('.fw-fan__link') : [];

    function setOpen(open) {
        fan.classList.toggle('fw-fan--open', open);
        toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
        toggle.setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation');

        // A closed fan must not be a set of invisible links sitting over the page.
        // CSS hides both panels with visibility, which keeps them out of the
        // accessibility tree and untappable; the links still need their own tab stop
        // taken away, since the list itself stays visible behind the button.
        links.forEach(function (a) { a.tabIndex = open ? 0 : -1; });

        if (open && links.length) links[0].focus();
    }

    function isOpen() { return fan.classList.contains('fw-fan--open'); }

    toggle.addEventListener('click', function () { setOpen(!isOpen()); });
    backdrop.addEventListener('click', function () { setOpen(false); });

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && isOpen()) {
            setOpen(false);
            toggle.focus();
        }
    });

    // A phone's back gesture should close the fan before it leaves the page, which is
    // what someone who opened it by accident expects it to do.
    toggle.addEventListener('click', function () {
        if (isOpen()) history.pushState({ fwFan: true }, '');
    });

    window.addEventListener('popstate', function () {
        if (isOpen()) setOpen(false);
    });

    // Returning to a page from the back/forward cache restores the markup as it was
    // left, so an open fan would come back open over a page nobody navigated to.
    window.addEventListener('pageshow', function () { setOpen(false); });
})();
