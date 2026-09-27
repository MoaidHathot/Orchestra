import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import ViewerModal from './ViewerModal';
import type { Orchestration } from '../../types';

vi.mock('../../mermaid', () => ({
  renderMermaidDag: vi.fn(async () => {}),
  renderExecutionDag: vi.fn(async () => {}),
}));

vi.mock('../../api', () => ({
  api: {
    get: vi.fn(),
    post: vi.fn(),
    delete: vi.fn(),
    put: vi.fn(),
    patch: vi.fn(),
  },
  describeError: (err: unknown) => (err instanceof Error ? err.message : String(err)),
}));

import { api } from '../../api';

const mockedGet = api.get as unknown as ReturnType<typeof vi.fn>;

function mockDetail(detail: Orchestration) {
  mockedGet.mockImplementation(async (url: string) => {
    if (url.endsWith('/tags')) return { orchestrationId: detail.id, effectiveTags: [], authorTags: [], hostTags: [] };
    if (url === '/api/tags') return { count: 0, tags: [] };
    return detail;
  });
}

const base: Orchestration = {
  id: 'o1',
  name: 'demo',
  description: 'A demo',
  steps: [{ name: 's1', type: 'Command', command: 'echo', maxOutputBytes: 4096 }],
};

async function openDetails(detail: Orchestration) {
  mockDetail(detail);
  render(<ViewerModal open orchestration={detail} onClose={() => {}} />);
  await waitFor(() => expect(mockedGet).toHaveBeenCalled());
  fireEvent.click(screen.getByText('Details'));
  await waitFor(() => expect(screen.getByText('A demo')).toBeInTheDocument());
}

describe('ViewerModal execution settings', () => {
  beforeEach(() => {
    Element.prototype.scrollIntoView = vi.fn();
    mockedGet.mockReset();
  });

  it('is hidden when the orchestration leaves everything to the host defaults', async () => {
    await openDetails({ ...base, onStepFailure: 'continue' });

    expect(screen.queryByTestId('execution-settings')).not.toBeInTheDocument();
  });

  it('shows maxConcurrency, onStepFailure=stop, the default model and the timeout', async () => {
    await openDetails({ ...base, defaultModel: 'claude-opus-4.8', timeoutSeconds: 900, maxConcurrency: 3, onStepFailure: 'stop' });

    const section = screen.getByTestId('execution-settings');
    expect(section).toHaveTextContent('claude-opus-4.8');
    expect(section).toHaveTextContent('900s');
    expect(section).toHaveTextContent('3 steps at a time');
    expect(section).toHaveTextContent('stop');
    expect(section).toHaveTextContent('cancel every unfinished step');
  });

  it('uses singular wording for a serialised orchestration', async () => {
    await openDetails({ ...base, maxConcurrency: 1 });

    expect(screen.getByTestId('execution-settings')).toHaveTextContent('1 step at a time');
  });
});
