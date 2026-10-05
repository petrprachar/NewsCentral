// UI-1/UI-2: sets the DOM `indeterminate` property on a checkbox — not reflected as an HTML
// attribute, so it cannot be set from Blazor markup and must go through JS interop. Used by
// Components/Shared/TriStateCheckbox.razor (and, before UI-2, directly by CreateAssignment.razor's
// own master checkbox — now refactored onto the same shared component).
window.ncSetIndeterminate = function (element, value) {
    if (element) {
        element.indeterminate = value;
    }
};
