import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';
import RunAnnotationEditor from './RunAnnotationEditor';
import { api } from '../api';

vi.mock('../api', () => ({
  api: {
    get: vi.fn(),
    patch: vi.fn(),
    post: vi.fn(),
    put: vi.fn(),
    delete: vi.fn(),
  },
}));

const mockedApi = vi.mocked(api);

const ANNOTATION_URL = '/api/history/my-orch/run123/annotation';

function mockLoad(annotation: unknown, tags: Array<{ tag: string; count: number }> = []) {
  mockedApi.get.mockImplementation(async (url: string) => {
    if (url === ANNOTATION_URL) {
      if (annotation === null) throw new Error('404');
      return annotation;
    }
    if (url === '/api/history/annotations') return { tags };
    throw new Error(`unexpected url ${url}`);
  });
}

function renderEditor(onChanged = vi.fn()) {
  return render(
    <RunAnnotationEditor orchestrationName="my-orch" runId="run123" onChanged={onChanged} />,
  );
}

describe('RunAnnotationEditor', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    vi.resetAllMocks();
  });

  it('loads and displays an existing annotation', async () => {
    mockLoad({
      runId: 'run123',
      favorite: true,
      title: 'Connect evidence pack',
      tags: ['connect'],
      note: 'Counts unreliable.',
    });

    renderEditor();

    expect(await screen.findByDisplayValue('Connect evidence pack')).toBeInTheDocument();
    expect(screen.getByDisplayValue('Counts unreliable.')).toBeInTheDocument();
    expect(screen.getByText('connect')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /remove favorite/i })).toHaveAttribute('aria-pressed', 'true');
  });

  it('treats a missing annotation as the empty state, not an error', async () => {
    mockLoad(null);

    renderEditor();

    await waitFor(() => expect(screen.getByText('No tags')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: /mark as favorite/i })).toHaveAttribute('aria-pressed', 'false');
  });

  it('toggles the favorite flag via PATCH', async () => {
    mockLoad(null);
    mockedApi.patch.mockResolvedValue({ runId: 'run123', favorite: true, tags: [] });

    renderEditor();

    fireEvent.click(await screen.findByRole('button', { name: /mark as favorite/i }));

    await waitFor(() =>
      expect(mockedApi.patch).toHaveBeenCalledWith(ANNOTATION_URL, { favorite: true }));
  });

  it('saves the title and note together without touching tags', async () => {
    mockLoad({ runId: 'run123', favorite: false, tags: ['keep'], title: null, note: null });
    mockedApi.patch.mockResolvedValue({ runId: 'run123', favorite: false, tags: ['keep'], title: 'New title' });

    renderEditor();

    const titleInput = await screen.findByLabelText('Title');
    fireEvent.change(titleInput, { target: { value: 'New title' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(mockedApi.patch).toHaveBeenCalledWith(ANNOTATION_URL, { title: 'New title', note: '' }));
    // Crucially the payload carries no `tags` key, so the server leaves them alone.
    const payload = mockedApi.patch.mock.calls[0][1] as Record<string, unknown>;
    expect(payload).not.toHaveProperty('tags');
  });

  it('adds a tag on Enter and preserves existing tags', async () => {
    mockLoad({ runId: 'run123', favorite: false, tags: ['keep'] });
    mockedApi.patch.mockResolvedValue({ runId: 'run123', favorite: false, tags: ['keep', 'connect'] });

    renderEditor();

    const input = await screen.findByLabelText('Add a tag');
    fireEvent.change(input, { target: { value: 'Connect' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    // Lower-cased client-side to match server normalization.
    await waitFor(() =>
      expect(mockedApi.patch).toHaveBeenCalledWith(ANNOTATION_URL, { tags: ['keep', 'connect'] }));
  });

  it('ignores a duplicate tag', async () => {
    mockLoad({ runId: 'run123', favorite: false, tags: ['connect'] });

    renderEditor();

    const input = await screen.findByLabelText('Add a tag');
    fireEvent.change(input, { target: { value: 'connect' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => expect(input).toHaveValue(''));
    expect(mockedApi.patch).not.toHaveBeenCalled();
  });

  it('removes a tag', async () => {
    mockLoad({ runId: 'run123', favorite: false, tags: ['connect', 'keep'] });
    mockedApi.patch.mockResolvedValue({ runId: 'run123', favorite: false, tags: ['keep'] });

    renderEditor();

    fireEvent.click(await screen.findByRole('button', { name: 'Remove tag connect' }));

    await waitFor(() =>
      expect(mockedApi.patch).toHaveBeenCalledWith(ANNOTATION_URL, { tags: ['keep'] }));
  });

  it('offers known tags that are not already applied', async () => {
    mockLoad(
      { runId: 'run123', favorite: false, tags: ['connect'] },
      [{ tag: 'connect', count: 3 }, { tag: 'quarterly', count: 1 }],
    );

    renderEditor();

    await waitFor(() => expect(screen.getByText('quarterly')).toBeInTheDocument());
    // 'connect' appears once as an applied chip, not repeated as a suggestion.
    expect(screen.getAllByText('connect')).toHaveLength(1);
  });

  it('notifies the parent after a successful change', async () => {
    const onChanged = vi.fn();
    mockLoad(null);
    mockedApi.patch.mockResolvedValue({ runId: 'run123', favorite: true, tags: [] });

    renderEditor(onChanged);

    fireEvent.click(await screen.findByRole('button', { name: /mark as favorite/i }));

    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  it('surfaces a save failure instead of failing silently', async () => {
    mockLoad(null);
    mockedApi.patch.mockRejectedValue(new Error('server exploded'));

    renderEditor();

    fireEvent.click(await screen.findByRole('button', { name: /mark as favorite/i }));

    expect(await screen.findByText('server exploded')).toBeInTheDocument();
  });
});
