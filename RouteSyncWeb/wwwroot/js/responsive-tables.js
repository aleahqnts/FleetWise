// Names each cell after its column, so a table can be read as a stack of cards on a
// phone where its columns will not fit.
//
// The name is copied from the table's own header rather than written onto each cell in
// the markup. Three of these tables fetch their rows after the page loads and rebuild
// them on every refresh, so a hand-written label would have to be repeated in the
// server view and again in the script that replaces it. Reading the header keeps one
// source: change a column heading and the card follows it.
(function () {
    function label(table) {
        var heads = [];
        table.querySelectorAll('thead th').forEach(function (th) {
            heads.push(th.textContent.trim());
        });
        if (!heads.length) return;

        table.querySelectorAll('tbody tr').forEach(function (tr) {
            Array.prototype.forEach.call(tr.children, function (cell, i) {
                // A cell spanning the table is a message rather than a value: the empty
                // state, or a spinner. Naming it after the first column would be a lie.
                if (cell.colSpan > 1) {
                    cell.removeAttribute('data-label');
                    return;
                }
                if (heads[i]) cell.setAttribute('data-label', heads[i]);
                else cell.removeAttribute('data-label');
            });
        });
    }

    function labelAll() {
        document.querySelectorAll('table.rs-cards').forEach(label);
    }

    // Cards that open. A tap anywhere on the card that is not a control shows the
    // rest of its values, so a list of twenty vehicles reads as twenty headings
    // rather than a hundred and twenty lines.
    document.addEventListener('click', function (e) {
        var row = e.target.closest && e.target.closest('.rs-collapse tbody tr');
        if (!row) return;
        // A button, a link or a field is doing its own job; opening the card on top
        // of that would fight it.
        if (e.target.closest('button, a, input, select, textarea, label')) return;
        row.classList.toggle('rs-open');
    });

    document.addEventListener('DOMContentLoaded', function () {
        labelAll();

        // Rows replaced by a fetch or by the live refresh arrive unlabelled. Watching for
        // added rows covers both without either having to know this exists.
        //
        // Only childList is watched, never attributes, so writing the labels cannot
        // trigger the observer that wrote them.
        var observer = new MutationObserver(function (records) {
            var tables = [];
            records.forEach(function (r) {
                var table = r.target.closest && r.target.closest('table.rs-cards');
                if (table && tables.indexOf(table) === -1) tables.push(table);
            });
            tables.forEach(label);
        });

        document.querySelectorAll('table.rs-cards tbody').forEach(function (tbody) {
            observer.observe(tbody, { childList: true, subtree: true });
        });
    });
})();
