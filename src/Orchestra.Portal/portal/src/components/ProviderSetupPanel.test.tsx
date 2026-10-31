import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import ProviderSetupPanel from './ProviderSetupPanel';
import { workspaceRequest } from '../workspaceApi';
vi.mock('../workspaceApi', () => ({ workspaceRequest: vi.fn() }));
afterEach(cleanup);
describe('provider setup', () => {
  it('surfaces device instructions and discovered models through the job API', async () => {
    const provider = { provider: 'copilot', installed: true, ready: false, detail: 'Sign in', models: [], integrations: [] };
    vi.mocked(workspaceRequest).mockImplementation(async path => {
      if (path === '/providers') return { providers: [provider], jobs: [] };
      if (path.includes('/operations')) return { id: 'job', provider: 'copilot', state: 'running', progress: { message: 'Starting' } };
      return { id: 'job', provider: 'copilot', state: 'running', progress: { message: 'Authorize this device', authorizationUrl: 'https://github.com/login/device', userCode: 'ABCD-1234' } };
    });
    render(<ProviderSetupPanel settings={{ configPath: '/c', source: 'Project', revision: '1', values: {}, effective: {}, overrides: {}, restartRequired: [] }} onSaved={() => {}} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Sign in to Copilot' }));
    expect(await screen.findByText('ABCD-1234')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Open sign-in page' })).toHaveAttribute('href', 'https://github.com/login/device');
    fireEvent.click(screen.getByRole('button', { name: 'Cancel setup' }));
    await waitFor(() => expect(workspaceRequest).toHaveBeenCalledWith('/jobs/job/cancel', {}));
  });

  it('preserves environment-backed model defaults when only the provider changes', async () => {
    vi.mocked(workspaceRequest).mockReset();
    vi.mocked(workspaceRequest).mockResolvedValue({ providers: [], jobs: [] });
    render(<ProviderSetupPanel settings={{ configPath: '/c', source: 'Project', revision: '1', values: { defaultModel: 'env:MODEL' }, effective: { defaultProvider: 'copilot', defaultModel: 'chosen-model' }, overrides: {}, restartRequired: [] }} onSaved={() => {}} />);
    fireEvent.click(screen.getByRole('button', { name: 'Use provider and model' }));
    await waitFor(() => expect(workspaceRequest).toHaveBeenCalledWith('/settings', { revision: '1', changes: {} }));
  });

  it('renders native conditional auth fields and submits only applicable answers', async () => {
    vi.mocked(workspaceRequest).mockReset();
    const provider = { provider: 'opencode', installed: true, ready: false, detail: 'Choose an integration', models: [], integrations: [{ id: 'test', name: 'Test integration', connected: false, methods: [{ id: 'login', type: 'oauth', label: 'OAuth', form: [
      { key: 'kind', title: 'Account kind', type: 'string', default: 'personal', options: [{ value: 'personal', label: 'Personal' }, { value: 'enterprise', label: 'Enterprise' }] },
      { key: 'domain', title: 'Enterprise domain', type: 'string', required: true, when: [{ key: 'kind', op: 'eq', value: 'enterprise' }] },
    ] }] }] };
    vi.mocked(workspaceRequest).mockImplementation(async path => path === '/providers' ? { providers: [provider], jobs: [] } : { id: 'done', state: 'succeeded', progress: { message: 'Connected' } });
    render(<ProviderSetupPanel settings={{ configPath: '/c', source: 'Project', revision: '1', values: {}, effective: { defaultProvider: 'opencode', defaultModel: 'chosen-model' }, overrides: {}, restartRequired: [] }} onSaved={() => {}} />);
    await screen.findByText('Test integration');
    fireEvent.change(screen.getByLabelText('Model integration'), { target: { value: 'test' } });
    fireEvent.change(screen.getByLabelText('Sign-in method'), { target: { value: 'login' } });
    expect(screen.queryByLabelText('Enterprise domain')).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Account kind'), { target: { value: 'enterprise' } });
    fireEvent.change(screen.getByLabelText('Enterprise domain'), { target: { value: 'example.test' } });
    fireEvent.change(screen.getByLabelText('Account kind'), { target: { value: 'personal' } });
    fireEvent.click(screen.getByRole('button', { name: 'Connect integration' }));
    await waitFor(() => expect(workspaceRequest).toHaveBeenCalledWith('/providers/opencode/operations', expect.objectContaining({ answer: { kind: 'personal' } })));
  });
});
