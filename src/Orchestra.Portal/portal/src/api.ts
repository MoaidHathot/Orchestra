/** Simple in-memory cache for GET responses. */
const responseCache = new Map<string, { data: unknown; timestamp: number }>();

/** Cache TTL in milliseconds (5 minutes). */
const CACHE_TTL = 5 * 60 * 1000;

/** Maximum number of retry attempts. */
const MAX_RETRIES = 3;

/** Base delay for exponential backoff (ms). */
const BASE_DELAY = 1_000;

/**
 * A non-2xx response from the Orchestra API, with the RFC 7807 problem-details fields the host
 * emits (`application/problem+json`) lifted onto the error so UI code can show `detail` or
 * `title` instead of a raw JSON blob. Plain-text and `{ error }` bodies are normalised too.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail?: string;
  readonly type?: string;
  readonly url: string;
  /** Extra members of the problem document (e.g. `reason`, `stepName`). */
  readonly extensions: Record<string, unknown>;

  constructor(init: {
    status: number;
    title: string;
    detail?: string;
    type?: string;
    url: string;
    extensions?: Record<string, unknown>;
  }) {
    super(init.detail && init.detail.length > 0 ? init.detail : init.title);
    this.name = 'ApiError';
    this.status = init.status;
    this.title = init.title;
    this.detail = init.detail;
    this.type = init.type;
    this.url = init.url;
    this.extensions = init.extensions ?? {};
  }

  /** Short, human-readable text suitable for a toast or inline error. */
  get userMessage(): string {
    return this.message;
  }
}

const PROBLEM_FIELDS = new Set(['type', 'title', 'status', 'detail', 'instance', 'traceId']);

/**
 * Builds an {@link ApiError} from a failed response. Understands `application/problem+json`
 * (title/detail/status), the legacy `{ "error": "..." }` shape used by a few portal endpoints,
 * and falls back to the raw text (or the HTTP status text when the body is empty).
 */
export async function toApiError(res: Response, url: string): Promise<ApiError> {
  const status = res.status;
  const fallbackTitle = res.statusText && res.statusText.length > 0 ? res.statusText : `HTTP ${status}`;

  let text = '';
  try {
    text = await res.text();
  } catch {
    /* body already consumed or unreadable */
  }

  const trimmed = text.trim();
  if (trimmed.startsWith('{')) {
    try {
      const body = JSON.parse(trimmed) as Record<string, unknown>;
      const title = typeof body.title === 'string' ? body.title : undefined;
      const detail = typeof body.detail === 'string' ? body.detail : undefined;
      const legacyError = typeof body.error === 'string' ? body.error : undefined;
      const type = typeof body.type === 'string' ? body.type : undefined;

      if (title || detail || legacyError) {
        const extensions: Record<string, unknown> = {};
        for (const [k, v] of Object.entries(body)) {
          if (!PROBLEM_FIELDS.has(k) && k !== 'error') extensions[k] = v;
        }
        return new ApiError({
          status,
          title: title ?? legacyError ?? fallbackTitle,
          detail: detail ?? (title ? undefined : legacyError),
          type,
          url,
          extensions,
        });
      }
    } catch {
      /* not JSON after all — fall through to the raw text */
    }
  }

  return new ApiError({
    status,
    title: fallbackTitle,
    detail: trimmed.length > 0 ? trimmed : undefined,
    url,
  });
}

/** Text to show a user for any thrown value (ApiError, Error, or unknown). */
export function describeError(err: unknown): string {
  if (err instanceof ApiError) return err.userMessage;
  if (err instanceof Error) return err.message;
  return String(err);
}

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
          item.reject(await toApiError(res, item.url));
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

/** Immediate form operations: no hidden offline queue or automatic mutation retry. */
export async function requestJson<T>(url: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, {
    method: body === undefined ? 'GET' : 'POST', signal, cache: 'no-store',
    ...(body !== undefined ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {}),
  });
  if (!response.ok) throw await toApiError(response, url);
  const text = await response.text();
  return text ? JSON.parse(text) as T : undefined as T;
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
      if (!res.ok) throw await toApiError(res, url);
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
    if (!res.ok) throw await toApiError(res, url);
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
    if (!res.ok) throw await toApiError(res, url);
    return res.json();
  },

  /**
   * PATCH with retry (not queued offline - used for immediate partial updates).
   * Distinct from PUT: omitted fields are left untouched rather than cleared.
   */
  async patch<T = unknown>(url: string, body?: unknown): Promise<T> {
    const res = await fetchWithRetry(url, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!res.ok) throw await toApiError(res, url);
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
    if (!res.ok) throw await toApiError(res, url);
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
