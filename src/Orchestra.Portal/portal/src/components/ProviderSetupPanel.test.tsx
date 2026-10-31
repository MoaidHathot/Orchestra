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
});
