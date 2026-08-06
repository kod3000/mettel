// Tiny JS shim called by InventoryGrid. Kept to one function because the
// whole point of the WASM app is to minimize the JS layer — anything not
// strictly needed lives in C# instead.
window.bruinScroll = {
    // Returns { scrolled, total } where scrolled is the pixel offset of
    // the viewport bottom and total is scrollHeight. The grid uses these
    // to decide when to fetch the next keyset page.
    metrics: (el) => el ? ({ scrolled: el.scrollTop + el.clientHeight, total: el.scrollHeight }) : ({ scrolled: 0, total: 0 })
};
