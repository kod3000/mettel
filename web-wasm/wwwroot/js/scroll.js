// Tiny JS helpers called by Blazor components. The WASM app's whole
// point is to minimize the JS layer — anything not strictly needed
// (browser APIs the CLR can't reach) lives here.
window.bruinScroll = {
    // Returns { scrolled, total } where scrolled = viewport-bottom offset
    // and total = scrollHeight. The grid uses these to decide when to
    // fetch the next keyset page.
    metrics: (el) => el
        ? ({ scrolled: el.scrollTop + el.clientHeight, total: el.scrollHeight })
        : ({ scrolled: 0, total: 0 })
};

window.bruinDownload = {
    // Save a byte[] (base64 from Blazor) as `filename`. Used by the
    // CSV template + sample buttons in BulkUploadPanel.
    fromBase64: (base64, filename, mime) => {
        const bin = atob(base64);
        const bytes = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        const blob = new Blob([bytes], { type: mime || "application/octet-stream" });
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
    },
    // Open a URL with the given tenant key as an X-Api-Key request. Since
    // the CSV endpoints stream the response, we fetch → blob → save
    // rather than window.open (which can't set headers).
    // Sends Accept: text/csv so /bulk-jobs/{id}/errors returns CSV instead
    // of its default JSON — otherwise we'd save a JSON body with a .csv
    // extension. Template and sample endpoints ignore the header (they
    // always emit text/csv) so it's safe for all callers.
    fromUrl: async (url, apiKey, filename) => {
        const res = await fetch(url, {
            headers: { "X-Api-Key": apiKey, "Accept": "text/csv" },
        });
        if (!res.ok) return;
        const blob = await res.blob();
        const objectUrl = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = objectUrl;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(objectUrl);
    }
};

// Clipboard write with a graceful fallback for browsers that block the
// async clipboard API (older Safari on non-HTTPS origins). Returns true
// on success. Used by ApiReferencePanel's Copy buttons.
window.bruinClipboard = {
    write: async (text) => {
        try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                await navigator.clipboard.writeText(text);
                return true;
            }
        } catch { /* fall through */ }
        try {
            const ta = document.createElement("textarea");
            ta.value = text;
            ta.style.position = "fixed";
            ta.style.opacity = "0";
            document.body.appendChild(ta);
            ta.select();
            const ok = document.execCommand("copy");
            ta.remove();
            return ok;
        } catch { return false; }
    }
};

// Confirm-before-leave. Matches the React app's beforeunload prompt so
// tab-close / hard-refresh / cross-origin nav asks "Leave site?". The
// browser shows its own text; the empty returnValue is the trigger.
window.bruinBeforeUnload = {
    _handler: null,
    enable: () => {
        if (window.bruinBeforeUnload._handler) return;
        const h = (e) => { e.preventDefault(); e.returnValue = ""; };
        window.addEventListener("beforeunload", h);
        window.bruinBeforeUnload._handler = h;
    },
    disable: () => {
        const h = window.bruinBeforeUnload._handler;
        if (!h) return;
        window.removeEventListener("beforeunload", h);
        window.bruinBeforeUnload._handler = null;
    }
};
