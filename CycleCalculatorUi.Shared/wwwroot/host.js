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
export function loadDraft(key) {
    try { return localStorage.getItem(key); } catch { return null; }
}

export function saveDraft(key, content) {
    try { localStorage.setItem(key, content); } catch { /* ignore */ }
}
