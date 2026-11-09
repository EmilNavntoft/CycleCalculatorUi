// Small browser helpers for file download and the autosaved draft.

export function downloadText(name, content) {
    const blob = new Blob([content], { type: "application/octet-stream" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// Storage can be unavailable (private windows, blocked site data); the editor works without a draft.
// Returned as bytes so .NET reads it as a stream: one SignalR message from the browser may be at most 32 KB,
// and documents with embedded compressor models are larger.
export function loadDraft(key) {
    let text = null;
    try { text = localStorage.getItem(key); } catch { /* ignore */ }
    return text === null ? null : new TextEncoder().encode(text);
}

export function saveDraft(key, content) {
    try { localStorage.setItem(key, content); } catch { /* ignore */ }
}
