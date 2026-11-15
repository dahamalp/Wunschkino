export function focusCard(id) {
    document.querySelector(`[data-item-id="${CSS.escape(id)}"]`)?.focus();
}
