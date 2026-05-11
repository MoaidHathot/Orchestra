import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import WaitingInputsModal from './WaitingInputsModal';
import { api } from '../../api';
import type { PendingInputRecord } from '../../types';

function makeRecord(overrides: Partial<PendingInputRecord> = {}): PendingInputRecord {
	return {
		orchestrationName: 'approval-deploy',
		runId: 'run-abc123',
		stepName: 'review-deploy',
		kind: 'Approval',
		prompt: 'Approve deploy?',
		choices: ['approve', 'reject'],
		createdAt: new Date(Date.now() - 60_000).toISOString(),
		...overrides,
	};
}

describe('WaitingInputsModal', () => {
	beforeEach(() => {
		localStorage.clear();
		api.clearCache();
	});

	afterEach(() => {
		vi.restoreAllMocks();
	});

	it('renders the empty state when no records are pending', () => {
		render(
			<WaitingInputsModal open={true} onClose={() => {}} records={[]} loading={false} onResponded={() => {}} />,
		);
		expect(screen.getByText(/All caught up/i)).toBeInTheDocument();
	});

	it('renders the spinner while loading', () => {
		const { container } = render(
			<WaitingInputsModal open={true} onClose={() => {}} records={[]} loading={true} onResponded={() => {}} />,
		);
		expect(container.querySelector('.spinner')).toBeInTheDocument();
	});

	it('lists every record and selects the first by default', () => {
		const records = [
			makeRecord({ runId: 'r-1', stepName: 'review' }),
			makeRecord({ runId: 'r-2', stepName: 'deploy', orchestrationName: 'orch-b' }),
		];
		const { container } = render(
			<WaitingInputsModal open={true} onClose={() => {}} records={records} loading={false} onResponded={() => {}} />,
		);

		// First record is selected by default — runId appears inside the detail pane's <code> element.
		const detailCode = container.querySelector('.waiting-inputs-detail code');
		expect(detailCode).not.toBeNull();
		expect(detailCode!.textContent).toBe('r-1');

		// Both step names appear in the list rows.
		expect(screen.getAllByText('review').length).toBeGreaterThan(0);
		expect(screen.getByText('deploy')).toBeInTheDocument();
		// Both orchestrations show up
		expect(screen.getAllByText('approval-deploy').length).toBeGreaterThan(0);
		expect(screen.getByText('orch-b')).toBeInTheDocument();
	});

	it('renders radio buttons for each choice', () => {
		render(
			<WaitingInputsModal
				open={true}
				onClose={() => {}}
				records={[makeRecord()]}
				loading={false}
				onResponded={() => {}}
			/>,
		);
		expect(screen.getByLabelText('approve')).toBeInTheDocument();
		expect(screen.getByLabelText('reject')).toBeInTheDocument();
	});

	it('submits the selected choice via POST /respond and notifies parent on success', async () => {
		const fetchSpy = vi.fn(async () => ({
			ok: true,
			status: 200,
			headers: new Headers({ 'Content-Type': 'application/json' }),
			async json() { return { accepted: true }; },
			async text() { return '{"accepted":true}'; },
		} as unknown as Response));
		globalThis.fetch = fetchSpy as unknown as typeof fetch;

		const onResponded = vi.fn();
		render(
			<WaitingInputsModal
				open={true}
				onClose={() => {}}
				records={[makeRecord()]}
				loading={false}
				onResponded={onResponded}
			/>,
		);

		// Switch from default "approve" to "reject"
		fireEvent.click(screen.getByLabelText('reject'));
		fireEvent.click(screen.getByRole('button', { name: /Submit response/i }));

		await waitFor(() => expect(onResponded).toHaveBeenCalledTimes(1));
		expect(onResponded).toHaveBeenCalledWith('approval-deploy', 'run-abc123', 'review-deploy');

		// Verify the request URL + body
		const [url, init] = fetchSpy.mock.calls[0] as [string, RequestInit];
		expect(url).toBe('/api/orchestrations/approval-deploy/runs/run-abc123/respond?step=review-deploy');
		expect(init.method).toBe('POST');
		const body = JSON.parse(init.body as string);
		expect(body.choice).toBe('reject');
	});

	it('submits a free-form reply when no choices exist', async () => {
		const fetchSpy = vi.fn(async () => ({
			ok: true,
			status: 200,
			headers: new Headers(),
			async json() { return { accepted: true }; },
			async text() { return ''; },
		} as unknown as Response));
		globalThis.fetch = fetchSpy as unknown as typeof fetch;

		const onResponded = vi.fn();
		render(
			<WaitingInputsModal
				open={true}
				onClose={() => {}}
				records={[makeRecord({ kind: 'EngineTool', choices: undefined })]}
				loading={false}
				onResponded={onResponded}
			/>,
		);

		fireEvent.change(screen.getByPlaceholderText(/Type your response/i), {
			target: { value: 'Looks good to me.' },
		});
		fireEvent.click(screen.getByRole('button', { name: /Submit response/i }));

		await waitFor(() => expect(onResponded).toHaveBeenCalledTimes(1));
		const [, init] = fetchSpy.mock.calls[0] as [string, RequestInit];
		const body = JSON.parse(init.body as string);
		expect(body.reply).toBe('Looks good to me.');
		expect(body.choice).toBeNull();
	});

	it('shows an inline validation error when neither choice nor reply is set', async () => {
		const onResponded = vi.fn();
		render(
			<WaitingInputsModal
				open={true}
				onClose={() => {}}
				records={[makeRecord({ kind: 'EngineTool', choices: undefined })]}
				loading={false}
				onResponded={onResponded}
			/>,
		);

		fireEvent.click(screen.getByRole('button', { name: /Submit response/i }));

		expect(await screen.findByRole('alert')).toHaveTextContent(/pick a choice or write a reply/i);
		expect(onResponded).not.toHaveBeenCalled();
	});

	it('persists respondedBy to localStorage on first submission', async () => {
		const fetchSpy = vi.fn(async () => ({
			ok: true,
			status: 200,
			headers: new Headers(),
			async json() { return { accepted: true }; },
			async text() { return ''; },
		} as unknown as Response));
		globalThis.fetch = fetchSpy as unknown as typeof fetch;

		render(
			<WaitingInputsModal
				open={true}
				onClose={() => {}}
				records={[makeRecord()]}
				loading={false}
				onResponded={() => {}}
			/>,
		);

		fireEvent.change(screen.getByPlaceholderText('alice'), { target: { value: 'maya' } });
		fireEvent.click(screen.getByRole('button', { name: /Submit response/i }));

		await waitFor(() => expect(fetchSpy).toHaveBeenCalled());
		expect(localStorage.getItem('orchestra.portal.respondedBy')).toBe('maya');

		const [, init] = fetchSpy.mock.calls[0] as [string, RequestInit];
		const body = JSON.parse(init.body as string);
		expect(body.respondedBy).toBe('maya');
	});

	it('drops the record when the server returns 404 (already resolved race)', async () => {
		const fetchSpy = vi.fn(async () => ({
			ok: false,
			status: 404,
			headers: new Headers(),
			async json() { return { error: 'No pending input record' }; },
			async text() { return 'No pending input record for this run.'; },
		} as unknown as Response));
		globalThis.fetch = fetchSpy as unknown as typeof fetch;

		const onResponded = vi.fn();
		render(
			<WaitingInputsModal
				open={true}
				onClose={() => {}}
				records={[makeRecord()]}
				loading={false}
				onResponded={onResponded}
			/>,
		);

		fireEvent.click(screen.getByRole('button', { name: /Submit response/i }));

		await waitFor(() => expect(onResponded).toHaveBeenCalledTimes(1));
		expect(onResponded).toHaveBeenCalledWith('approval-deploy', 'run-abc123', 'review-deploy');
		expect(await screen.findByRole('alert')).toHaveTextContent(/already resolved/i);
	});
});
