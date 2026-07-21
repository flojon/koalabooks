window.koala = {
    // Deferred to the next animation frame: under WebAssembly render mode, the .NET call
    // and the browser's own native Tab-focus-move default action race on the same task, and
    // the native action wins, silently overwriting this focus. Waiting a frame lets any
    // pending native default action settle first.
    focusId: (id) => requestAnimationFrame(() => document.getElementById(id)?.focus())
};

window.downloadFileFromBase64 = function (base64, fileName) {
    if (!base64 || !fileName) return;
    const link = document.createElement('a');
    link.href = 'data:application/octet-stream;base64,' + base64;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};
