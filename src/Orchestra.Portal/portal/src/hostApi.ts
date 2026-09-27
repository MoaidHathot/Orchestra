import { api, toApiError } from './api';
import type { HealthReport, LogTail, PruneResult } from './types';

/**
 * Typed client for the host-diagnostics endpoints behind the Portal's Host panel:
 * health, the rolling log file, and on-demand run retention.
 */
export const hostApi = {
  /**
   * `GET /api/health`. Unlike the generic client this does NOT treat a 503 as a failure: an
   * unhealthy host answers 503 *with* the report body, which is exactly what the panel wants to
   * show. Only a missing/unparseable body or a network error throws.
   */
  async getHealth(): Promise<HealthReport> {
    const url = '/api/health';
    const res = await fetch(url, { headers: { Accept: 'application/json' } });
    if (res.ok || res.status === 503) {
      const text = await res.text();
      try {
        const body = JSON.parse(text) as Partial<HealthReport>;
        if (body && typeof body.status === 'string' && Array.isArray(body.checks)) {
          return body as HealthReport;
        }
      } catch {
        /* fall through to the error below */
      }
    }
    throw await toApiError(res, url);
  },

  /** `GET /api/logs?lines=N` - the last N lines of the newest log file. */
  getLogTail(lines: number): Promise<LogTail> {
    return api.get<LogTail>(`/api/logs?lines=${Math.max(0, Math.floor(lines))}`);
  },

  /** `GET /api/logs?offset=&file=` - everything appended since a previous response. */
  getLogSince(offset: number, file: string): Promise<LogTail> {
    return api.get<LogTail>(`/api/logs?offset=${Math.max(0, Math.floor(offset))}&file=${encodeURIComponent(file)}`);
  },

  /**
   * `POST /api/history/prune`. With no overrides the host's configured retention applies;
   * `maxRuns` / `maxAgeDays` replace it for this call. `dryRun` previews without deleting.
   */
  pruneRuns(options: { dryRun: boolean; maxRuns?: number | null; maxAgeDays?: number | null }): Promise<PruneResult> {
    const query: string[] = [];
    if (options.dryRun) query.push('dryRun=true');
    if (options.maxRuns != null) query.push(`maxRuns=${Math.max(0, Math.floor(options.maxRuns))}`);
    if (options.maxAgeDays != null) query.push(`maxAgeDays=${Math.max(0, Math.floor(options.maxAgeDays))}`);
    const url = '/api/history/prune' + (query.length > 0 ? `?${query.join('&')}` : '');
    return api.post<PruneResult>(url, {});
  },
};

/**
 * Renders a .NET `TimeSpan` string (`d.hh:mm:ss.fffffff` or `hh:mm:ss.fffffff`) as a short
 * human duration such as `2d 3h`, `3h 12m`, `12m 5s` or `5s`. Returns the input on parse failure.
 */
export function formatUptime(timeSpan: string): string {
  const match = /^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.\d+)?$/.exec(timeSpan.trim());
  if (!match) return timeSpan;
  const days = Number(match[1] ?? 0);
  const hours = Number(match[2]);
  const minutes = Number(match[3]);
  const seconds = Number(match[4]);
  if (days > 0) return `${days}d ${hours}h`;
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m ${seconds}s`;
  return `${seconds}s`;
}
