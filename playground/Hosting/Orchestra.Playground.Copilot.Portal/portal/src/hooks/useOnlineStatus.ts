import { useState, useEffect, useCallback } from 'react';

export interface OnlineStatus {
  /** Whether the browser reports being online (navigator.onLine). */
  isOnline: boolean;
  /** Whether the server is actually reachable (last health-check succeeded). */
  isServerReachable: boolean;
  /** Timestamp of the last successful server contact. */
  lastOnline: number | null;
}

/**
 * Tracks both browser online/offline state and actual server reachability.
 * Polls `/api/status` at the given interval to detect server-down scenarios
 * even when the browser thinks it's online.
 */
export function useOnlineStatus(pollIntervalMs = 10_000): OnlineStatus {
  const [isOnline, setIsOnline] = useState(navigator.onLine);
  const [isServerReachable, setIsServerReachable] = useState(true);
  const [lastOnline, setLastOnline] = useState<number | null>(Date.now());

  // Browser online/offline events
  useEffect(() => {
    const handleOnline = () => setIsOnline(true);
    const handleOffline = () => setIsOnline(false);
    window.addEventListener('online', handleOnline);
    window.addEventListener('offline', handleOffline);
    return () => {
      window.removeEventListener('online', handleOnline);
      window.removeEventListener('offline', handleOffline);
    };
  }, []);

  // Server reachability check
  const checkServer = useCallback(async () => {
    if (!navigator.onLine) {
      setIsServerReachable(false);
      return;
    }
    try {
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 5_000);
      const res = await fetch('/api/status', { signal: controller.signal });
      clearTimeout(timeout);
      if (res.ok) {
        setIsServerReachable(true);
        setLastOnline(Date.now());
      } else {
        setIsServerReachable(false);
      }
    } catch {
      setIsServerReachable(false);
    }
  }, []);

  useEffect(() => {
    checkServer();
    const interval = setInterval(checkServer, pollIntervalMs);
    return () => clearInterval(interval);
  }, [checkServer, pollIntervalMs]);

  // When browser goes online, immediately re-check server
  useEffect(() => {
    if (isOnline) {
      checkServer();
    }
  }, [isOnline, checkServer]);

  return { isOnline, isServerReachable, lastOnline };
}
