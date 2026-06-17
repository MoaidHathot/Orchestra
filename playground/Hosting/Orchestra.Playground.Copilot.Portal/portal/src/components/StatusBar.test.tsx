import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import StatusBar from './StatusBar';
import type { ServerStatus } from '../types';
import type { OnlineStatus } from '../hooks/useOnlineStatus';

vi.mock('../api', () => ({
  api: { pendingMutations: 0 },
}));

const onlineStatus: OnlineStatus = {
  isOnline: true,
  isServerReachable: true,
  lastOnline: Date.now(),
};

function makeStatus(overrides: Partial<ServerStatus> = {}): ServerStatus {
  return {
    outlook: null,
    orchestrationCount: 4,
    activeTriggers: 2,
    runningExecutions: 1,
    agentRuntime: null,
    ...overrides,
  };
}

describe('StatusBar', () => {
  it('renders agent runtime CLI and session counts', () => {
    render(
      <StatusBar
        status={makeStatus({
          agentRuntime: {
            provider: 'copilot',
            activePools: 2,
            cliInstances: 3,
            activeSessions: 5,
          },
        })}
        onlineStatus={onlineStatus}
      />,
    );

    expect(screen.getByText('3 CLI')).toBeInTheDocument();
    expect(screen.getByText('5 sessions')).toBeInTheDocument();
    expect(screen.getByTitle('2 active copilot pool(s)')).toBeInTheDocument();
  });

  it('renders one item per provider when agentRuntimes is present', () => {
    render(
      <StatusBar
        status={makeStatus({
          agentRuntimes: [
            { provider: 'copilot', activePools: 1, cliInstances: 2, activeSessions: 3 },
            { provider: 'opencode', activePools: 1, cliInstances: 1, activeSessions: 1 },
          ],
        })}
        onlineStatus={onlineStatus}
      />,
    );

    expect(screen.getByText('copilot')).toBeInTheDocument();
    expect(screen.getByText('opencode')).toBeInTheDocument();
    expect(screen.getByTitle('1 active copilot pool(s)')).toBeInTheDocument();
    expect(screen.getByTitle('1 active opencode pool(s)')).toBeInTheDocument();
  });
});
