/**
 * Client-side mirror of `GET /api/config`: values the server wants every client to agree on.
 *
 * The Portal loads it once at startup (App.tsx) and components read it synchronously through
 * the getters below, so a modal opened before the request completes still gets a sane value.
 */

export interface ServerConfig {
	polling: {
		activeExecutionsMs: number;
		orchestrationsMs: number;
		historyMs: number;
		serverStatusMs: number;
	};
	/** The model to pre-fill when an author has not picked one (host default, else engine fallback). */
	defaultModel?: string | null;
	/** The host's default agent provider, when configured. */
	defaultProvider?: string | null;
}

/**
 * Mirrors `Orchestra.Engine.ModelDefaults.Model`. Only used until `/api/config` answers (or if it
 * never does); the server value always wins once loaded.
 */
export const FALLBACK_DEFAULT_MODEL = 'claude-opus-4.8';

export const DEFAULT_POLLING: ServerConfig['polling'] = {
	activeExecutionsMs: 1000,
	orchestrationsMs: 5000,
	historyMs: 5000,
	serverStatusMs: 5000,
};

let current: ServerConfig = { polling: DEFAULT_POLLING, defaultModel: null, defaultProvider: null };

export function setServerConfig(config: Partial<ServerConfig> | null | undefined): void {
	if (!config) return;
	current = {
		polling: { ...DEFAULT_POLLING, ...(config.polling ?? {}) },
		defaultModel: config.defaultModel ?? current.defaultModel ?? null,
		defaultProvider: config.defaultProvider ?? current.defaultProvider ?? null,
	};
}

export function getServerConfig(): ServerConfig {
	return current;
}

/** The model new Prompt steps should default to. Never empty. */
export function getDefaultModel(): string {
	const m = current.defaultModel?.trim();
	return m && m.length > 0 ? m : FALLBACK_DEFAULT_MODEL;
}

/** Test hook: reset to the pre-load state. */
export function resetServerConfigForTests(): void {
	current = { polling: DEFAULT_POLLING, defaultModel: null, defaultProvider: null };
}
