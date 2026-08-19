// Focus helper for the six code boxes on the password reset screen.
// Blazor owns the values; only the caret needs to move from here.
window.otpFocus = function (index) {
    var boxes = document.querySelectorAll('.code-box');
    if (boxes[index]) {
        boxes[index].focus();
        boxes[index].select();
    }
};

// Force one box back to the value the component holds. Needed only when the typed
// character was rejected (a letter, say): Blazor sees no change in its own model
// and so would leave the stray character sitting in the DOM.
window.otpSet = function (index, value) {
    var boxes = document.querySelectorAll('.code-box');
    if (boxes[index]) boxes[index].value = value || '';
};

// Pasting a whole code into one box. The boxes hold a single character each, so
// the browser would otherwise keep the first digit and drop the rest. The digits
// are handed to the component, which owns the values and re-renders every box.
window.otpAttachPaste = function (dotNetRef) {
    var wrap = document.querySelector('.code-boxes');
    if (!wrap || wrap.dataset.pasteBound === '1') return;
    wrap.dataset.pasteBound = '1';

    wrap.addEventListener('paste', function (e) {
        var text = (e.clipboardData || window.clipboardData)?.getData('text') || '';
        var digits = text.replace(/\D/g, '').slice(0, 6);
        if (!digits) return;
        e.preventDefault();
        dotNetRef.invokeMethodAsync('PasteCode', digits);
    });
};
