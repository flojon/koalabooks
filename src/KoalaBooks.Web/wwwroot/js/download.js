window.koala = {
    focusId: (id) => document.getElementById(id)?.focus()
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
