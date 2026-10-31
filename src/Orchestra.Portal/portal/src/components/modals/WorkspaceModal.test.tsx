import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import WorkspaceModal from './WorkspaceModal';
import { workspaceRequest, type SettingsSnapshot } from '../../workspaceApi';
vi.mock('../../workspaceApi', () => ({ workspaceRequest: vi.fn() }));
const request = vi.mocked(workspaceRequest);
const snapshot: SettingsSnapshot = { configPath: '/work/orchestra.json', source: 'Project', revision: 'v1', values: {}, effective: { defaultProvider: 'copilot', defaultModel: 'model', dataPath: '/data', defaultMaxConcurrency: 4 }, overrides: {}, restartRequired: [] };
const info = { workspaceDirectory: '/work', workflowDirectory: '/work/orchestrations', dataPath: '/data', showWelcome: true, defaultProvider: 'copilot', defaultModel: 'model', templates: [] };
const props = { onClose: vi.fn(), onRun: vi.fn(), onImport: vi.fn(), onDiagnostics: vi.fn(), onChanged: vi.fn() };
beforeEach(() => { vi.clearAllMocks(); request.mockImplementation(async path => path === '' ? info : snapshot); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
describe('workspace onboarding', () => {
  it('offers a demo, authoring and provider setup without credentials', async () => {
    render(<WorkspaceModal initialTab="welcome" {...props} />);
    expect(await screen.findByRole('button', { name: 'Try the demo' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Create workflow' })).toBeDisabled();
    fireEvent.change(screen.getByLabelText('What would you like to do?'), { target: { value: 'Create a report' } });
    expect(screen.getByRole('button', { name: 'Create workflow' })).toBeEnabled();
    expect(screen.getByText('/work/orchestrations')).toBeInTheDocument();
  });
  it('prepares the demo and opens its normal execution view', async () => {
    request.mockImplementation(async path => path === '' ? info : path === '/settings' ? snapshot : { id: 'demo', name: 'smoke-test', path: '/work/demo.yaml' });
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ id: 'demo', name: 'smoke-test', steps: [] }))));
    render(<WorkspaceModal initialTab="welcome" {...props} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Try the demo' }));
    await waitFor(() => expect(props.onRun).toHaveBeenCalledWith(expect.objectContaining({ id: 'demo' }), {}));
  });
  it('sends only edited settings with the revision and surfaces restart information', async () => {
    request.mockImplementation(async (path, body) => body ? { ...snapshot, revision: 'v2', restartRequired: ['scan'] } : path === '' ? info : snapshot);
    render(<WorkspaceModal initialTab="execution" {...props} />);
    const concurrency = await screen.findByLabelText('Maximum concurrent steps per run');
    fireEvent.change(concurrency, { target: { value: '2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));
    await waitFor(() => expect(request).toHaveBeenCalledWith('/settings', { revision: 'v1', changes: { defaultMaxConcurrency: 2 } }));
    expect(await screen.findByText(/Saved changes require a Portal restart/)).toBeInTheDocument();
  });
  it('shows conflicts without silently discarding the edit', async () => {
    request.mockImplementation(async (path, body) => { if (body) throw new Error('Configuration changed; reload'); return path === '' ? info : snapshot; });
    render(<WorkspaceModal initialTab="execution" {...props} />);
    fireEvent.change(await screen.findByLabelText('Default model'), { target: { value: 'new-model' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Configuration changed');
    expect(screen.getByLabelText('Default model')).toHaveValue('new-model');
  });
});
