/**
 * Identity module: persists the user's display name in localStorage so HITL
 * responses submitted from the Portal include a stable <c>respondedBy</c> field
 * for the audit trail. Identity is purely advisory — the Portal currently has
 * no auth boundary, and the value can be edited from the Waiting Inputs modal.
 *
 * Storage key is namespaced (<c>orchestra.portal.respondedBy</c>) so it does
 * not collide with anything else the SPA may want to persist.
 */

const STORAGE_KEY = 'orchestra.portal.respondedBy';

/**
 * Returns the stored display name, or null if none has been set yet. Safe to
 * call during SSR (returns null when localStorage is unavailable).
 */
export function getRespondedBy(): string | null {
	try {
		const value = localStorage.getItem(STORAGE_KEY);
		if (value === null) return null;
		const trimmed = value.trim();
		return trimmed.length > 0 ? trimmed : null;
	} catch {
		return null;
	}
}

/**
 * Persists the supplied display name. Passing null/empty clears the entry.
 */
export function setRespondedBy(name: string | null): void {
	try {
		if (!name || name.trim().length === 0) {
			localStorage.removeItem(STORAGE_KEY);
			return;
		}
		localStorage.setItem(STORAGE_KEY, name.trim());
	} catch {
		// Storage quota / private mode — silently ignore. The user will simply
		// be re-prompted on the next response submission.
	}
}
