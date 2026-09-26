import { describe, it, expect, vi, afterEach } from 'vitest';
import { ApiError, api, describeError, toApiError } from './api';

function response(status: number, body: string, contentType = 'application/json', statusText = ''): Response {
	return new Response(body, {
		status,
		statusText,
		headers: { 'Content-Type': contentType },
	});
}

describe('toApiError', () => {
	it('lifts RFC 7807 problem-details fields onto the error', async () => {
		const res = response(
			404,
			JSON.stringify({
				type: 'https://tools.ietf.org/html/rfc7231#section-6.5.4',
				title: 'Orchestration not found',
				status: 404,
				detail: "No orchestration with id 'nope'.",
				orchestrationId: 'nope',
			}),
			'application/problem+json',
		);

		const err = await toApiError(res, '/api/orchestrations/nope');

		expect(err).toBeInstanceOf(ApiError);
		expect(err.status).toBe(404);
		expect(err.title).toBe('Orchestration not found');
		expect(err.detail).toBe("No orchestration with id 'nope'.");
		expect(err.message).toBe("No orchestration with id 'nope'.");
		expect(err.url).toBe('/api/orchestrations/nope');
		expect(err.extensions).toEqual({ orchestrationId: 'nope' });
	});

	it('uses the title when there is no detail', async () => {
		const err = await toApiError(response(409, JSON.stringify({ title: 'Profile already exists', status: 409 })), '/x');
		expect(err.message).toBe('Profile already exists');
		expect(err.detail).toBeUndefined();
	});

	it('understands the legacy { error } shape used by portal file endpoints', async () => {
		const err = await toApiError(response(400, JSON.stringify({ error: 'Directory not found: C:\\nope' })), '/api/browse');
		expect(err.status).toBe(400);
		expect(err.message).toBe('Directory not found: C:\\nope');
	});

	it('falls back to the raw text for non-JSON bodies', async () => {
		const err = await toApiError(response(500, 'something exploded', 'text/plain', 'Internal Server Error'), '/x');
		expect(err.title).toBe('Internal Server Error');
		expect(err.detail).toBe('something exploded');
		expect(err.message).toBe('something exploded');
	});

	it('falls back to the status when the body is empty', async () => {
		const err = await toApiError(response(503, '', 'text/plain'), '/x');
		expect(err.status).toBe(503);
		expect(err.message).toBe('HTTP 503');
	});

	it('never shows raw JSON to the user for a problem document', async () => {
		const err = await toApiError(response(400, JSON.stringify({ title: 'Bad', detail: 'because', status: 400 })), '/x');
		expect(err.userMessage).not.toContain('{');
	});
});

describe('describeError', () => {
	it('prefers the ApiError user message', () => {
		const err = new ApiError({ status: 400, title: 'Bad request', detail: 'name is required', url: '/x' });
		expect(describeError(err)).toBe('name is required');
	});

	it('handles plain errors and unknown values', () => {
		expect(describeError(new Error('boom'))).toBe('boom');
		expect(describeError('weird')).toBe('weird');
	});
});

describe('api.* throws ApiError on non-2xx', () => {
	const originalFetch = globalThis.fetch;
	afterEach(() => {
		globalThis.fetch = originalFetch;
		api.clearCache();
	});

	it('get rejects with an ApiError carrying problem details', async () => {
		globalThis.fetch = vi.fn(async () =>
			response(404, JSON.stringify({ title: 'Not found', detail: 'nothing here', status: 404 }), 'application/problem+json'),
		) as unknown as typeof fetch;

		await expect(api.get('/api/thing')).rejects.toMatchObject({
			name: 'ApiError',
			status: 404,
			message: 'nothing here',
		});
	});

	it('post rejects with an ApiError', async () => {
		globalThis.fetch = vi.fn(async () => response(400, JSON.stringify({ error: 'bad body' }))) as unknown as typeof fetch;

		await expect(api.post('/api/thing', {})).rejects.toBeInstanceOf(ApiError);
	});
});
