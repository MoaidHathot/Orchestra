/**
 * Vitest test setup. Installs a working in-memory `localStorage` shim because
 * Node 25 ships an experimental `localStorage` global whose methods are stubs
 * (no-op) and shadow jsdom's full implementation. Without this shim, every
 * test that touches `localStorage.setItem` / `getItem` / `clear` fails with
 * "X is not a function".
 */

class InMemoryStorage implements Storage {
	private store = new Map<string, string>();

	get length(): number {
		return this.store.size;
	}

	clear(): void {
		this.store.clear();
	}

	getItem(key: string): string | null {
		return this.store.get(key) ?? null;
	}

	key(index: number): string | null {
		return Array.from(this.store.keys())[index] ?? null;
	}

	removeItem(key: string): void {
		this.store.delete(key);
	}

	setItem(key: string, value: string): void {
		this.store.set(key, String(value));
	}
}

const shim = new InMemoryStorage();
Object.defineProperty(globalThis, 'localStorage', {
	configurable: true,
	value: shim,
});
if (typeof window !== 'undefined') {
	Object.defineProperty(window, 'localStorage', {
		configurable: true,
		value: shim,
	});
}
