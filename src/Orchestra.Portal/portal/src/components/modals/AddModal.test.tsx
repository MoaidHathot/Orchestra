import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import AddModal from './AddModal';
import { requestJson } from '../../api';

vi.mock('../../api', async original => ({ ...await original<typeof import('../../api')>(), requestJson: vi.fn() }));
vi.mock('../../mermaid', () => ({ renderMermaidDag: vi.fn() }));
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.clearAllMocks(); });
const file = { path: '/work/starter.yaml', fileName: 'starter.yaml', name: 'starter', valid: true };

describe('workflow import', () => {
  it('previews YAML with the backend parser and retains partial-import errors', async () => {
    const close = vi.fn();
    const added = vi.fn();
    vi.mocked(requestJson).mockImplementation(async path => path === '/api/folder/scan'
      ? { orchestrations: [file] }
      : path === '/api/orchestrations/preview'
        ? { json: '{"name":"starter"}', definition: { name: 'starter', steps: [{ name: 'yaml-answer', type: 'Transform' }] }, errors: [] }
        : { addedCount: 1, errors: [{ path: file.path, error: 'File was changed externally' }] });
    render(<AddModal open onAdded={added} onClose={close} />);
    fireEvent.change(screen.getByLabelText('Folder Path'), { target: { value: '/work' } });
    fireEvent.click(screen.getByRole('button', { name: 'Scan' }));
    fireEvent.click(await screen.findByRole('checkbox', { name: /starter.yaml/ }));
    expect(await screen.findByText('yaml-answer')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Add Orchestrations' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('File was changed externally');
    expect(added).toHaveBeenCalledOnce();
    expect(close).not.toHaveBeenCalled();
  });

  it('discards a late preview and previous selection when choosing another folder', async () => {
    let finishPreview!: (data: unknown) => void;
    vi.mocked(requestJson).mockImplementation(async path => path === '/api/folder/scan'
      ? { orchestrations: [file] } : new Promise(resolve => { finishPreview = resolve; }));
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ currentDirectory: '/different', entries: [] }))));
    render(<AddModal open onAdded={() => {}} onClose={() => {}} />);
    fireEvent.change(screen.getByLabelText('Folder Path'), { target: { value: '/work' } });
    fireEvent.click(screen.getByRole('button', { name: 'Scan' }));
    fireEvent.click(await screen.findByRole('checkbox', { name: /starter.yaml/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Choose a folder' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Use this folder' }));
    finishPreview({ json: '{}', definition: { name: 'stale-workflow' }, errors: [] });
    await waitFor(() => expect(screen.getByLabelText('Folder Path')).toHaveValue('/different'));
    expect(screen.queryByText('stale-workflow')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add Orchestrations' })).toBeDisabled();
  });
});
