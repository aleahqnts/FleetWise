// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// Pushes an identifier to the right of the name it belongs to, in a column shared
// by every entry in the list.
//
// A native option cannot be laid out: no columns, no alignment, the browser draws
// that popup itself. The only thing under our control is the text, so the name is
// padded to the width of the longest one with a space that does not collapse. The
// selects that use this are set in a monospace face, without which every character
// is a different width and the column drifts.
window.rsIdLabels = function (rows) {
    var widest = 0;
    rows.forEach(function (r) { if (r.name.length > widest) widest = r.name.length; });
    return rows.map(function (r) {
        var pad = new Array(widest - r.name.length + 3).join('\u00A0');
        return r.name + pad + r.id;
    });
};
