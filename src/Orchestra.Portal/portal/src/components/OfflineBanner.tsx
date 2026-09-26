import React from 'react';
import { Icons } from '../icons';
import type { OnlineStatus } from '../hooks/useOnlineStatus';
import { api } from '../api';

interface Props {
  onlineStatus: OnlineStatus;
}

export default function OfflineBanner({ onlineStatus }: Props): React.JSX.Element | null {
  const { isOnline, isServerReachable, lastOnline } = onlineStatus;

  // Fully online — don't render anything
  if (isOnline && isServerReachable) return null;

  const pendingCount = api.pendingMutations;

  let message: string;
  let severity: 'warning' | 'error';

  if (!isOnline) {
    message = 'You are offline. Showing cached data.';
    severity = 'error';
  } else {
    message = 'Server unreachable. Retrying automatically...';
    severity = 'warning';
  }

  const lastOnlineText = lastOnline
    ? `Last connected ${formatSecondsAgo(lastOnline)}`
    : '';

  return (
    <div
      className={`offline-banner offline-banner--${severity}`}
      role="alert"
      aria-live="assertive"
    >
      <div className="offline-banner__content">
        <Icons.AlertCircle aria-hidden="true" />
        <span className="offline-banner__message">{message}</span>
        {lastOnlineText && (
          <span className="offline-banner__last-online">{lastOnlineText}</span>
        )}
        {pendingCount > 0 && (
          <span className="offline-banner__pending">
            {pendingCount} pending {pendingCount === 1 ? 'action' : 'actions'} queued
          </span>
        )}
      </div>
    </div>
  );
}

function formatSecondsAgo(timestamp: number): string {
  const seconds = Math.floor((Date.now() - timestamp) / 1000);
  if (seconds < 10) return 'just now';
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h ago`;
}
