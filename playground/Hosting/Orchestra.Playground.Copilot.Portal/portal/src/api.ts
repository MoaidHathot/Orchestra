/** Simple in-memory cache for GET responses. */
const responseCache = new Map<string, { data: unknown; timestamp: number }>();

/** Cache TTL in milliseconds (5 minutes). */
const CACHE_TTL = 5 * 60 * 1000;

/** Maximum number of retry attempts. */
const MAX_RETRIES = 3;

/** Base delay for exponential backoff (ms). */
const BASE_DELAY = 1_000;

/** Pending mutations to replay when coming back online. */
interface QueuedMutation {
  method: 'POST' | 'DELETE';
  url: string;
  body?: unknown;
  resolve: (value: unknown) => void;
  reject: (reason: unknown) => void;
}

const mutationQueue: QueuedMutation[] = [];
let processingQueue = false;

function isOffline(): boolean {
  return !navigator.onLine;
}

async function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

/**
 * Wraps a fetch call with retry + exponential backoff.
 * Only retries on network errors and 5xx responses (not 4xx).
 */
async function fetchWithRetry(
  input: RequestInfo | URL,
  init?: RequestInit,
  retries = MAX_RETRIES,
): Promise<Response> {
  let lastError: unknown;
  for (let attempt = 0; attempt <= retries; attempt++) {
    try {
      const res = await fetch(input, init);
      // Don't retry client errors (4xx)
      if (res.status >= 400 && res.status < 500) {
        return res;
      }
      // Retry server errors (5xx)
      if (res.status >= 500 && attempt < retries) {
        await sleep(BASE_DELAY * Math.pow(2, attempt));
        continue;
      }
      return res;
    } catch (err) {
      lastError = err;
      if (attempt < retries && !isOffline()) {
        await sleep(BASE_DELAY * Math.pow(2, attempt));
      }
    }
  }
  throw lastError ?? new Error('Request failed after retries');
}

/** Returns cached data for a GET URL if still valid, or undefined. */
function getCached<T>(url: string): T | undefined {
  const entry = responseCache.get(url);
  if (!entry) return undefined;
  if (Date.now() - entry.timestamp > CACHE_TTL) {
    responseCache.delete(url);
    return undefined;
  }
  return entry.data as T;
}

/** Stores a GET response in the cache. */
function setCache(url: string, data: unknown): void {
  responseCache.set(url, { data, timestamp: Date.now() });
}

/** Process queued mutations when back online. */
async function processQueue(): Promise<void> {
  if (processingQueue || mutationQueue.length === 0) return;
  processingQueue = true;
  try {
    while (mutationQueue.length > 0 && !isOffline()) {
      const item = mutationQueue[0];
      try {
        const init: RequestInit = { method: item.method };
        if (item.body !== undefined) {
          init.headers = { 'Content-Type': 'application/json' };
          init.body = JSON.stringify(item.body);
        }
        const res = await fetchWithRetry(item.url, init);
        if (!res.ok) {
          item.reject(new Error(await res.text()));
        } else {
          item.resolve(await res.json());
        }
        mutationQueue.shift();
      } catch (err) {
        // If we went offline again, stop processing
        if (isOffline()) break;
        item.reject(err);
        mutationQueue.shift();
      }
    }
  } finally {
    processingQueue = false;
  }
}

// Auto-process queue when coming back online
if (typeof window !== 'undefined') {
  window.addEventListener('online', () => {
    processQueue();
  });
}

export const api = {
  /**
   * GET with retry and caching.
   * Returns cached data when offline; updates cache on success.
   */
  async get<T = unknown>(url: string): Promise<T> {
    // If offline, return cached data if available
    if (isOffline()) {
      const cached = getCached<T>(url);
      if (cached !== undefined) return cached;
      throw new Error('You are offline and no cached data is available');
    }

    try {
      const res = await fetchWithRetry(url);
      if (!res.ok) throw new Error(await res.text());
      const data: T = await res.json();
      setCache(url, data);
      return data;
    } catch (err) {
      // On network failure, try cache as fallback
      const cached = getCached<T>(url);
      if (cached !== undefined) return cached;
      throw err;
    }
  },

  /**
   * POST with retry. Queues the request if offline.
   */
  async post<T = unknown>(url: string, body?: unknown): Promise<T> {
    if (isOffline()) {
      return new Promise<T>((resolve, reject) => {
        mutationQueue.push({
          method: 'POST',
          url,
          body,
          resolve: resolve as (value: unknown) => void,
          reject,
        });
      });
    }

    const res = await fetchWithRetry(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!res.ok) throw new Error(await res.text());
    return res.json();
  },

  /**
   * PUT with retry (not queued offline — used for immediate updates).
   */
  async put<T = unknown>(url: string, body?: unknown): Promise<T> {
    const res = await fetchWithRetry(url, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!res.ok) throw new Error(await res.text());
    return res.json();
  },

  /**
   * DELETE with retry. Queues the request if offline.
   */
  async delete<T = unknown>(url: string): Promise<T> {
    if (isOffline()) {
      return new Promise<T>((resolve, reject) => {
        mutationQueue.push({
          method: 'DELETE',
          url,
          resolve: resolve as (value: unknown) => void,
          reject,
        });
      });
    }

    const res = await fetchWithRetry(url, { method: 'DELETE' });
    if (!res.ok) throw new Error(await res.text());
    return res.json();
  },

  /** Returns the number of queued offline mutations. */
  get pendingMutations(): number {
    return mutationQueue.length;
  },

  /** Clears the response cache. */
  clearCache(): void {
    responseCache.clear();
  },
};
