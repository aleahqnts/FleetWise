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
