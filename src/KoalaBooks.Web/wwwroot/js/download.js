window.koala = {
    focusId: (id) => document.getElementById(id)?.focus()
};

// Reads a Blazor DotNetStreamReference into a Blob and either triggers a file
// download (SIE export, VAT CSV) or opens it in a new tab (customer invoice
// PDF). Works identically whether the bytes came from an in-process Server
// call or a WASM-side REST fetch — the render mode is invisible from here.
window.koala.downloadFileFromStream = async function (streamRef, fileName, contentType, openInNewTab) {
    const buffer = await streamRef.arrayBuffer();
    const blob = new Blob([buffer], { type: contentType });
    const url = URL.createObjectURL(blob);

    if (openInNewTab) {
        window.open(url, '_blank');
    } else {
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    }

    // Delay revocation so window.open's new tab (and slower browsers'
    // download handling) have time to actually read the blob URL.
    setTimeout(() => URL.revokeObjectURL(url), 30000);
};
