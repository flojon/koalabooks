window.koala = {
    // Deferred to the next animation frame: under WebAssembly render mode, the .NET call
    // and the browser's own native Tab-focus-move default action race on the same task, and
    // the native action wins, silently overwriting this focus. Waiting a frame lets any
    // pending native default action settle first.
    focusId: (id) => requestAnimationFrame(() => document.getElementById(id)?.focus())
};

// Revokes a blob URL once we're reasonably sure it's no longer needed: either
// the tab regains focus (download dialog dismissed, or user switched back
// from the opened tab) or, failing that, a fallback timeout so the URL isn't
// held onto forever if focus never fires (e.g. a background download).
function revokeBlobUrlWhenDone(url) {
    let revoked = false;
    const revoke = () => {
        if (revoked) return;
        revoked = true;
        URL.revokeObjectURL(url);
        window.removeEventListener('focus', revoke);
        clearTimeout(fallback);
    };
    window.addEventListener('focus', revoke);
    const fallback = setTimeout(revoke, 60000);
}

// Reads a Blazor DotNetStreamReference into a Blob and either triggers a file
// download (SIE export, VAT CSV) or opens it in a new tab (customer invoice
// PDF). Works identically whether the bytes came from an in-process Server
// call or a WASM-side REST fetch — the render mode is invisible from here.
window.koala.downloadFileFromStream = async function (streamRef, fileName, contentType, openInNewTab) {
    // Open the tab synchronously, before any await, so browsers don't treat it
    // as an unrequested popup. Blob URLs carry no filename metadata, so the
    // title is set explicitly here — otherwise the tab would just show the
    // blob: URL.
    let newWindow = null;
    if (openInNewTab) {
        newWindow = window.open('', '_blank');
        if (newWindow) newWindow.document.title = fileName;
    }

    const buffer = await streamRef.arrayBuffer();
    // A File (not a plain Blob) is used because Chrome's built-in PDF viewer
    // reads the File's name for its own in-content title bar; a nameless Blob
    // falls back to showing the raw blob: URL there instead.
    const blob = new File([buffer], fileName, { type: contentType });
    const url = URL.createObjectURL(blob);

    if (openInNewTab) {
        if (newWindow) {
            // Embed via an iframe instead of navigating the window to the blob
            // URL: navigating replaces the document (and the title we just set)
            // with the browser's native PDF viewer, which has no title of its
            // own since blob URLs carry no filename.
            newWindow.document.body.style.margin = '0';
            const iframe = newWindow.document.createElement('iframe');
            iframe.src = url;
            iframe.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;border:none';
            newWindow.document.body.appendChild(iframe);
        } else {
            // Popup was blocked; fall back to opening after the fact.
            window.open(url, '_blank');
        }
    } else {
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    }

    revokeBlobUrlWhenDone(url);
};
