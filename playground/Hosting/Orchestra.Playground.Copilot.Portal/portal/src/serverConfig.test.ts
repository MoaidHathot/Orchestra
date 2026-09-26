import { describe, it, expect, beforeEach } from 'vitest';
import {
	DEFAULT_POLLING,
	FALLBACK_DEFAULT_MODEL,
	getDefaultModel,
	getServerConfig,
	resetServerConfigForTests,
	setServerConfig,
} from './serverConfig';

describe('serverConfig', () => {
	beforeEach(() => resetServerConfigForTests());

	it('falls back to the engine default model before /api/config has answered', () => {
		expect(getDefaultModel()).toBe(FALLBACK_DEFAULT_MODEL);
		expect(FALLBACK_DEFAULT_MODEL).toBe('claude-opus-4.8');
	});

	it('prefers the server-provided default model once loaded', () => {
		setServerConfig({ polling: DEFAULT_POLLING, defaultModel: 'gpt-5' });
		expect(getDefaultModel()).toBe('gpt-5');
	});

	it('ignores a blank server default model', () => {
		setServerConfig({ polling: DEFAULT_POLLING, defaultModel: '   ' });
		expect(getDefaultModel()).toBe(FALLBACK_DEFAULT_MODEL);
	});

	it('merges partial polling config over the defaults', () => {
		setServerConfig({ polling: { ...DEFAULT_POLLING, historyMs: 42 } });
		expect(getServerConfig().polling.historyMs).toBe(42);
		expect(getServerConfig().polling.activeExecutionsMs).toBe(DEFAULT_POLLING.activeExecutionsMs);
	});

	it('tolerates a null or undefined payload', () => {
		setServerConfig(null);
		setServerConfig(undefined);
		expect(getDefaultModel()).toBe(FALLBACK_DEFAULT_MODEL);
	});
});
