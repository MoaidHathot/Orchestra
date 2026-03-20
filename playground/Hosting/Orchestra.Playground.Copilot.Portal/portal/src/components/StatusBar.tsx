import React from 'react';
import { Icons } from '../icons';
import type { ServerStatus } from '../types';
import type { OnlineStatus } from '../hooks/useOnlineStatus';
import { api } from '../api';

interface Props {
  status: ServerStatus;
  onlineStatus: OnlineStatus;
}

export default function StatusBar({ status, onlineStatus }: Props): React.JSX.Element {
  const getOutlookStatusClass = (): string => {
    if (!status.outlook) return 'not-configured';
    switch (status.outlook.status) {
      case 'Connected':
        return 'connected';
      case 'Disconnected':
        return 'disconnected';
      case 'Connecting':
        return 'connecting';
      case 'NotConfigured':
        return 'not-configured';
      default:
        return 'not-configured';
    }
  };

  const getOutlookStatusText = (): string => {
    if (!status.outlook) return 'Not configured';
    switch (status.outlook.status) {
      case 'Connected':
        return 'Connected';
      case 'Disconnected':
        return 'Disconnected';
      case 'Connecting':
        return 'Connecting...';
      case 'NotConfigured':
        return 'Not configured';
      default:
        return status.outlook.status;
    }
  };

  const getConnectionClass = (): string => {
    if (!onlineStatus.isOnline) return 'offline';
    if (!onlineStatus.isServerReachable) return 'degraded';
    return 'online';
  };

  const getConnectionText = (): string => {
    if (!onlineStatus.isOnline) return 'Offline';
    if (!onlineStatus.isServerReachable) return 'Server unreachable';
    return 'Connected';
  };

  const pendingCount = api.pendingMutations;

  return (
    <div className="status-bar" role="contentinfo" aria-label="Application status">
      {/* Connection status */}
      <div className="status-bar-connection" title={getConnectionText()}>
        <span className={`status-indicator ${getConnectionClass()}`} />
        <span>{getConnectionText()}</span>
      </div>

      {/* Pending mutations indicator */}
      {pendingCount > 0 && (
        <div className="status-bar-item" title={`${pendingCount} queued action(s) waiting to sync`}>
          <span>{pendingCount} pending</span>
        </div>
      )}

      {/* Outlook status (only show if configured) */}
      {status.outlook && status.outlook.status !== 'NotConfigured' && (
        <div className="status-bar-item" title={status.outlook.errorMessage || ''}>
          <span className={`status-indicator ${getOutlookStatusClass()}`} />
          <span>Outlook: {getOutlookStatusText()}</span>
        </div>
      )}

      {/* Show error message if any */}
      {status.outlook?.errorMessage && (
        <div className="status-bar-error" title={status.outlook.errorMessage}>
          <Icons.X />
          <span
            style={{
              maxWidth: '300px',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {status.outlook.errorMessage}
          </span>
        </div>
      )}

      <div className="status-bar-spacer" />

      {/* Stats */}
      <div className="status-bar-item">
        <span>{status.orchestrationCount} orchestrations</span>
      </div>
      <div className="status-bar-item">
        <span>{status.activeTriggers} active triggers</span>
      </div>
      {status.runningExecutions > 0 && (
        <div className="status-bar-item">
          <span className="status-indicator running" style={{ background: 'var(--warning)' }} />
          <span>{status.runningExecutions} running</span>
        </div>
      )}
    </div>
  );
}
