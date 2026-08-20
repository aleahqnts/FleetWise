// Keyboard behaviour for stacked dialogs.
//
// Bootstrap listens for Escape on the dialog element, so a dialog that does not trap
// focus never sees the key until something inside is clicked. Several here deliberately
// do not trap it, to keep an overlay above them typeable.
//
// Dialogs built from plain markup have no key handling at all; they name their own close
// function in data-dialog-close. Escape always acts on the frontmost.
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
