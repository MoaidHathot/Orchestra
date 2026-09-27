import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { hostApi, formatUptime } from './hostApi';
import { api, ApiError } from './api';

function response(body: unknown, status = 200, contentType = 'application/json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': contentType } });
}

describe('hostApi', () => {
  let originalFetch: typeof globalThis.fetch;
  const calls: Array<{ url: string; method: string }> = [];

  beforeEach(() => {
    originalFetch = globalThis.fetch;
    calls.length = 0;
    api.clearCache();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  function mock(handler: (url: string) => Response) {
    globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      calls.push({ url, method: init?.method ?? 'GET' });
      return handler(url);
    }) as unknown as typeof fetch;
  }

  const report = { status: 'healthy', version: '0.9.1', timestamp: 't', uptime: '00:01:00', activeRuns: 0, checks: [] };

  it('getHealth returns the report on 200', async () => {
    mock(() => response(report));

    const result = await hostApi.getHealth();

    expect(result.status).toBe('healthy');
    expect(calls[0].url).toBe('/api/health');
  });

  it('getHealth returns the report on 503 too - an unhealthy host still describes itself', async () => {
    mock(() => response({ ...report, status: 'unhealthy', checks: [{ name: 'dataPath', status: 'unhealthy', detail: 'not writable' }] }, 503));

    const result = await hostApi.getHealth();

    expect(result.status).toBe('unhealthy');
    expect(result.checks[0].name).toBe('dataPath');
    expect(calls).toHaveLength(1);
  });

  it('getHealth throws an ApiError for other failures', async () => {
    mock(() => response({ title: 'Not Found', detail: 'nope', status: 404 }, 404, 'application/problem+json'));

    await expect(hostApi.getHealth()).rejects.toBeInstanceOf(ApiError);
  });

  it('getLogTail and getLogSince build the expected URLs', async () => {
    mock(() => response({ directory: 'd', file: 'orchestra-2026-09-27.log', path: 'p', sizeBytes: 1, offset: 1, truncated: false, enabled: true, lines: [] }));

    await hostApi.getLogTail(250);
    await hostApi.getLogSince(4096, 'orchestra-2026-09-27.log');

    expect(calls.map(c => c.url)).toEqual([
      '/api/logs?lines=250',
      '/api/logs?offset=4096&file=orchestra-2026-09-27.log',
    ]);
  });

  it('pruneRuns posts with only the parameters that are set', async () => {
    mock(() => response({ dryRun: true, policy: { source: 'request' }, wouldDelete: 0, runs: [] }));

    await hostApi.pruneRuns({ dryRun: true });
    await hostApi.pruneRuns({ dryRun: false, maxRuns: 50 });
    await hostApi.pruneRuns({ dryRun: true, maxRuns: 10, maxAgeDays: 30 });

    expect(calls.map(c => `${c.method} ${c.url}`)).toEqual([
      'POST /api/history/prune?dryRun=true',
      'POST /api/history/prune?maxRuns=50',
      'POST /api/history/prune?dryRun=true&maxRuns=10&maxAgeDays=30',
    ]);
  });
});

describe('formatUptime', () => {
  it.each([
    ['00:00:05.1234567', '5s'],
    ['00:12:05', '12m 5s'],
    ['03:12:05.5', '3h 12m'],
    ['2.03:12:05.5', '2d 3h'],
    ['garbage', 'garbage'],
  ])('%s -> %s', (input, expected) => {
    expect(formatUptime(input)).toBe(expected);
  });
});
