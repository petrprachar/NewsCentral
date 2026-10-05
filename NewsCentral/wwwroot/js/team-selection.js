// UI-1: sets the DOM `indeterminate` property on a checkbox — not reflected as an HTML
// attribute, so it cannot be set from Blazor markup and must go through JS interop. Used by
// CreateAssignment.razor's "Select all" master checkbox for the Other Teams list.
window.ncSetIndeterminate = function (element, value) {
    if (element) {
        element.indeterminate = value;
    }
};
