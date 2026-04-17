import { useEffect, useRef } from 'react';

/**
 * Event payload shapes pushed from the backend DashboardEventBroadcaster.
 */
export interface DashboardEventHandlers {
  /** Fired when profile activation/deactivation changes the effective active orchestration set
   *  (scheduler transitions, manual activate/deactivate, etc.). */
  onProfileActiveSetChanged?: (evt: {
    activatedOrchestrationIds: string[];
    deactivatedOrchestrationIds: string[];
    trigger: string;
  }) => void;

  /** Fired when any orchestration execution starts (manual, schedule, webhook, resume, ...). */
  onExecutionStarted?: (evt: {
    executionId: string;
    orchestrationId: string;
    orchestrationName: string;
    triggeredBy: string;
  }) => void;

  /** Fired when any orchestration execution reaches a terminal state. */
  onExecutionCompleted?: (evt: {
    executionId: string;
    orchestrationId: string;
    orchestrationName: string;
    status: string;
  }) => void;

  /** Fired once when the stream is first established (useful for triggering a full refresh). */
  onConnected?: () => void;
}

/**
 * Opens a single long-lived EventSource to /api/events to receive real-time dashboard
 * notifications (profile activation, execution lifecycle). Automatically reconnects on
 * network errors with exponential backoff.
 *
 * This replaces the need for fast polling to detect backend-driven state changes.
 */
export function useDashboardEvents(handlers: DashboardEventHandlers): void {
  // Keep handlers in a ref so we don't have to re-open the stream whenever the caller
  // passes a new closure (common in React).
  const handlersRef = useRef(handlers);
  useEffect(() => {
    handlersRef.current = handlers;
  }, [handlers]);

  useEffect(() => {
    let closed = false;
    let eventSource: EventSource | null = null;
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    let backoffMs = 1_000; // start at 1s, cap at 30s
    const MAX_BACKOFF_MS = 30_000;

    const connect = () => {
      if (closed) return;

      eventSource = new EventSource('/api/events');

      eventSource.addEventListener('open', () => {
        backoffMs = 1_000; // reset backoff on successful open
      });

      eventSource.addEventListener('connected', () => {
        handlersRef.current.onConnected?.();
      });

      eventSource.addEventListener('profile-active-set-changed', (e) => {
        try {
          const data = JSON.parse((e as MessageEvent).data);
          handlersRef.current.onProfileActiveSetChanged?.(data);
        } catch (err) {
          console.error('Failed to parse profile-active-set-changed event:', err);
        }
      });

      eventSource.addEventListener('execution-started', (e) => {
        try {
          const data = JSON.parse((e as MessageEvent).data);
          handlersRef.current.onExecutionStarted?.(data);
        } catch (err) {
          console.error('Failed to parse execution-started event:', err);
        }
      });

      eventSource.addEventListener('execution-completed', (e) => {
        try {
          const data = JSON.parse((e as MessageEvent).data);
          handlersRef.current.onExecutionCompleted?.(data);
        } catch (err) {
          console.error('Failed to parse execution-completed event:', err);
        }
      });

      // heartbeat events are ignored — they exist just to keep the connection alive

      eventSource.addEventListener('error', () => {
        // EventSource auto-reconnects by default, but when the server is down we want
        // an explicit close + backoff so we don't hammer it.
        if (closed) return;
        if (eventSource && eventSource.readyState === EventSource.CLOSED) {
          scheduleReconnect();
        }
      });
    };

    const scheduleReconnect = () => {
      if (closed) return;
      try { eventSource?.close(); } catch { /* ignore */ }
      eventSource = null;
      reconnectTimer = setTimeout(() => {
        backoffMs = Math.min(backoffMs * 2, MAX_BACKOFF_MS);
        connect();
      }, backoffMs);
    };

    connect();

    return () => {
      closed = true;
      if (reconnectTimer) clearTimeout(reconnectTimer);
      try { eventSource?.close(); } catch { /* ignore */ }
      eventSource = null;
    };
  }, []); // intentionally empty — handlers are read via ref
}
