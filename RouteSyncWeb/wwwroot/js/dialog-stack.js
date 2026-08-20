// Keyboard behaviour for dialogs across the dashboard.
//
// Two problems this solves, both of which appear once dialogs stack.
//
// Bootstrap listens for Escape on the dialog element itself. A dialog that does not
// trap focus, which several here deliberately do not so an overlay above them stays
// typeable, therefore never sees the key until something inside it has been clicked.
// Focusing each dialog as it opens, and handing focus back to whatever remains open
// when one closes, keeps Escape working without a click in between.
//
// Dialogs built from plain markup rather than Bootstrap have no key handling at all.
// They opt in by naming their own close function in data-dialog-close, and the
// topmost one is closed first, so Escape always acts on what is in front.
(function () {
    function isVisible(el) {
        return el && getComputedStyle(el).display !== 'none';
    }

    function depth(el) {
        return parseInt(getComputedStyle(el).zIndex, 10) || 0;
    }

    /** Every dialog currently on screen, Bootstrap's and this project's own. */
    function openDialogs() {
        var bootstrapDialogs = Array.prototype.slice.call(document.querySelectorAll('.modal.show'));
        var plainDialogs = Array.prototype.slice
            .call(document.querySelectorAll('[data-dialog-close]'))
            .filter(isVisible);
        return bootstrapDialogs.concat(plainDialogs);
    }

    /** The one in front, which is the one the keyboard belongs to. */
    function frontDialog() {
        var open = openDialogs();
        if (!open.length) return null;
        return open.reduce(function (a, b) { return depth(b) >= depth(a) ? b : a; });
    }

    function focusFront() {
        var front = frontDialog();
        if (front && front.classList.contains('modal')) front.focus();
    }

    document.addEventListener('shown.bs.modal', function (e) { e.target.focus(); });
    document.addEventListener('hidden.bs.modal', focusFront);

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape') return;

        var front = frontDialog();
        // A Bootstrap dialog in front closes itself, provided it has focus, which the
        // handlers above see to.
        if (!front || !front.hasAttribute('data-dialog-close')) return;

        var close = window[front.getAttribute('data-dialog-close')];
        if (typeof close !== 'function') return;

        e.preventDefault();
        e.stopPropagation();
        close();
        focusFront();
    }, true);

    // Reachable from page scripts that close a dialog by their own route.
    window.focusFrontDialog = focusFront;
})();
