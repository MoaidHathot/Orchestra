import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Icons } from '../../icons';
import { useFocusTrap } from '../../hooks/useFocusTrap';
import { hostApi, formatUptime } from '../../hostApi';
import { describeError } from '../../api';
import type { HealthReport, LogTail, PruneResult } from '../../types';

type Tab = 'health' | 'logs' | 'maintenance';

interface Props {
  open: boolean;
  onClose: () => void;
  /** Called after a real (non-dry-run) prune so the dashboard can refresh Recent Executions. */
  onRunsPruned?: (result: PruneResult) => void;
  /** Tab to open with; defaults to health. */
  initialTab?: Tab;
}

/** Poll cadence for the health tab and for log follow mode. */
export const HOST_MODAL_REFRESH_MS = 3_000;
/** Lines requested for the initial log tail. */
export const LOG_TAIL_LINES = 300;

/**
 * Host panel: readiness checks (`/api/health`), the rolling log file (`/api/logs`) with a
 * follow mode, and on-demand run retention (`/api/history/prune`) with a mandatory preview.
 */
export default function HostModal({ open, onClose, onRunsPruned, initialTab = 'health' }: Props): React.JSX.Element {
  const trapRef = useFocusTrap<HTMLDivElement>(open, onClose);
  const [tab, setTab] = useState<Tab>(initialTab);

  useEffect(() => {
    if (open) setTab(initialTab);
  }, [open, initialTab]);

  return (
    <div className={`modal-overlay ${open ? 'visible' : ''}`} ref={trapRef}>
      <div className="modal modal-lg" role="dialog" aria-modal="true" aria-label="Host">
        <div className="modal-header">
          <div className="modal-title">
            <Icons.Heart /> Host
          </div>
          <button className="modal-close" aria-label="Close" onClick={onClose}><Icons.X /></button>
        </div>
        <div className="tabs" role="tablist">
          <button role="tab" aria-selected={tab === 'health'} className={`tab ${tab === 'health' ? 'active' : ''}`} onClick={() => setTab('health')}>Health</button>
          <button role="tab" aria-selected={tab === 'logs'} className={`tab ${tab === 'logs' ? 'active' : ''}`} onClick={() => setTab('logs')}>Logs</button>
          <button role="tab" aria-selected={tab === 'maintenance'} className={`tab ${tab === 'maintenance' ? 'active' : ''}`} onClick={() => setTab('maintenance')}>Maintenance</button>
        </div>
        <div className="modal-body" style={{ minHeight: '360px' }}>
          {open && tab === 'health' && <HealthTab />}
          {open && tab === 'logs' && <LogsTab />}
          {open && tab === 'maintenance' && <MaintenanceTab onRunsPruned={onRunsPruned} />}
        </div>
      </div>
    </div>
  );
}

// ── Health ───────────────────────────────────────────────────────────────────

export function HealthTab(): React.JSX.Element {
  const [report, setReport] = useState<HealthReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    try {
      setReport(await hostApi.getHealth());
      setError(null);
    } catch (err) {
      setError(describeError(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
    const interval = setInterval(() => { void load(); }, HOST_MODAL_REFRESH_MS);
    return () => clearInterval(interval);
  }, [load]);

  if (loading && !report) {
    return <div className="empty-state"><div className="spinner"></div></div>;
  }

  return (
    <div data-testid="health-tab">
      {error && (
        <div className="host-error" role="alert"><Icons.Warning /> {error}</div>
      )}
      {report && (
        <>
          <div className={`host-health-summary ${report.status}`} data-testid="health-summary">
            <span className={`status-indicator ${statusIndicatorClass(report.status)}`} />
            <strong>{report.status}</strong>
            <span className="text-muted">v{report.version} - up {formatUptime(report.uptime)} - {report.activeRuns} active run{report.activeRuns === 1 ? '' : 's'}</span>
            <button className="btn btn-sm" style={{ marginLeft: 'auto' }} onClick={() => { void load(); }} title="Refresh now"><Icons.Refresh /></button>
          </div>
          <table className="host-checks" data-testid="health-checks">
            <tbody>
              {report.checks.map(check => (
                <tr key={check.name} className={`host-check ${check.status}`}>
                  <td className="host-check-status"><span className={`status-indicator ${statusIndicatorClass(check.status)}`} /></td>
                  <td className="host-check-name">{check.name}</td>
                  <td className="host-check-detail">{check.detail}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  );
}

function statusIndicatorClass(status: string): string {
  switch (status) {
    case 'healthy': return 'online';
    case 'degraded': return 'degraded';
    case 'unhealthy': return 'offline';
    default: return 'not-configured';
  }
}

// ── Logs ─────────────────────────────────────────────────────────────────────

export function LogsTab(): React.JSX.Element {
  const [tail, setTail] = useState<LogTail | null>(null);
  const [lines, setLines] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [follow, setFollow] = useState(true);
  const [filter, setFilter] = useState('');
  const cursorRef = useRef<{ offset: number; file: string } | null>(null);
  const bottomRef = useRef<HTMLDivElement | null>(null);

  const loadTail = useCallback(async () => {
    try {
      const result = await hostApi.getLogTail(LOG_TAIL_LINES);
      setTail(result);
      setLines(result.lines);
      cursorRef.current = { offset: result.offset, file: result.file };
      setError(null);
    } catch (err) {
      setError(describeError(err));
    } finally {
      setLoading(false);
    }
  }, []);

  const loadSince = useCallback(async () => {
    const cursor = cursorRef.current;
    if (!cursor) return;
    try {
      const result = await hostApi.getLogSince(cursor.offset, cursor.file);
      if (result.file !== cursor.file) {
        // The day rolled: the response is the top of the new file.
        setLines(prev => [...prev, `--- log rolled to ${result.file} ---`, ...result.lines]);
      } else if (result.lines.length > 0) {
        setLines(prev => {
          const next = [...prev, ...result.lines];
          // Keep the buffer bounded so a chatty host cannot grow the DOM without limit.
          return next.length > 5_000 ? next.slice(next.length - 5_000) : next;
        });
      }
      cursorRef.current = { offset: result.offset, file: result.file };
      setTail(result);
      setError(null);
    } catch (err) {
      setError(describeError(err));
    }
  }, []);

  useEffect(() => { void loadTail(); }, [loadTail]);

  useEffect(() => {
    if (!follow) return;
    const interval = setInterval(() => { void loadSince(); }, HOST_MODAL_REFRESH_MS);
    return () => clearInterval(interval);
  }, [follow, loadSince]);

  useEffect(() => {
    if (follow) bottomRef.current?.scrollIntoView?.({ block: 'end' });
  }, [lines, follow]);

  const visible = filter.trim().length === 0
    ? lines
    : lines.filter(line => line.toLowerCase().includes(filter.trim().toLowerCase()));

  return (
    <div data-testid="logs-tab" className="host-logs">
      <div className="host-logs-toolbar">
        <input
          type="search"
          className="form-input"
          placeholder="Filter lines..."
          aria-label="Filter log lines"
          value={filter}
          onChange={e => setFilter(e.target.value)}
        />
        <label className="host-logs-follow">
          <input type="checkbox" checked={follow} onChange={e => setFollow(e.target.checked)} /> Follow
        </label>
        <button className="btn btn-sm" onClick={() => { void loadTail(); }} title="Reload the tail"><Icons.Refresh /></button>
        {tail && (
          <span className="text-muted host-logs-file" title={tail.path}>
            {tail.file}{tail.enabled ? '' : ' (file logging is off on this host)'}
          </span>
        )}
      </div>
      {error && <div className="host-error" role="alert"><Icons.Warning /> {error}</div>}
      {loading && !tail && <div className="empty-state"><div className="spinner"></div></div>}
      {!loading && !error && visible.length === 0 && (
        <div className="empty-state text-muted">{lines.length === 0 ? 'The log file is empty.' : 'No lines match the filter.'}</div>
      )}
      {visible.length > 0 && (
        <pre className="host-logs-output" data-testid="log-lines">
          {visible.map((line, i) => (
            <div key={i} className={`host-log-line ${levelClass(line)}`}>{line}</div>
          ))}
          <div ref={bottomRef} />
        </pre>
      )}
    </div>
  );
}

/** CSS class for a log line's bracketed level (`2026-09-27 10:15:42 [Warning] ...`). */
export function levelClass(line: string): string {
  const match = /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \[(\w+)\]/.exec(line);
  if (!match) return 'continuation';
  return `level-${match[1].toLowerCase()}`;
}

// ── Maintenance ──────────────────────────────────────────────────────────────

export function MaintenanceTab({ onRunsPruned }: { onRunsPruned?: (result: PruneResult) => void }): React.JSX.Element {
  const [useOverrides, setUseOverrides] = useState(false);
  const [maxRuns, setMaxRuns] = useState('');
  const [maxAgeDays, setMaxAgeDays] = useState('');
  const [preview, setPreview] = useState<PruneResult | null>(null);
  const [result, setResult] = useState<PruneResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const overrides = useOverrides
    ? {
        maxRuns: maxRuns.trim() === '' ? null : Number(maxRuns),
        maxAgeDays: maxAgeDays.trim() === '' ? null : Number(maxAgeDays),
      }
    : { maxRuns: null, maxAgeDays: null };

  const overridesValid = !useOverrides || (
    (overrides.maxRuns == null || (Number.isInteger(overrides.maxRuns) && overrides.maxRuns >= 0))
    && (overrides.maxAgeDays == null || (Number.isInteger(overrides.maxAgeDays) && overrides.maxAgeDays >= 0))
    && (overrides.maxRuns != null || overrides.maxAgeDays != null)
  );

  const runPreview = async () => {
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      setPreview(await hostApi.pruneRuns({ dryRun: true, ...overrides }));
    } catch (err) {
      setPreview(null);
      setError(describeError(err));
    } finally {
      setBusy(false);
    }
  };

  const runPrune = async () => {
    setBusy(true);
    setError(null);
    try {
      const outcome = await hostApi.pruneRuns({ dryRun: false, ...overrides });
      setResult(outcome);
      setPreview(null);
      onRunsPruned?.(outcome);
    } catch (err) {
      setError(describeError(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div data-testid="maintenance-tab" className="host-maintenance">
      <h3 style={{ marginTop: 0 }}>Prune run history</h3>
      <p className="text-muted">
        Applies run retention now instead of waiting for the host's hourly sweep. Favorited runs are never
        deleted. Preview first; nothing is removed until you confirm.
      </p>

      <label className="host-maintenance-toggle">
        <input
          type="checkbox"
          checked={useOverrides}
          onChange={e => { setUseOverrides(e.target.checked); setPreview(null); setResult(null); }}
        />
        Override the configured policy for this run
      </label>

      {useOverrides && (
        <div className="host-maintenance-overrides">
          <label>
            Keep newest
            <input
              type="number"
              min={0}
              className="form-input"
              aria-label="Max runs per orchestration"
              placeholder="e.g. 50"
              value={maxRuns}
              onChange={e => { setMaxRuns(e.target.value); setPreview(null); }}
            />
            runs per orchestration
          </label>
          <label>
            Delete runs older than
            <input
              type="number"
              min={0}
              className="form-input"
              aria-label="Max run age in days"
              placeholder="e.g. 30"
              value={maxAgeDays}
              onChange={e => { setMaxAgeDays(e.target.value); setPreview(null); }}
            />
            days
          </label>
          {!overridesValid && (
            <div className="text-muted">Enter a whole number of 0 or more for at least one rule (0 disables that rule).</div>
          )}
        </div>
      )}

      <div className="host-maintenance-actions">
        <button className="btn" onClick={() => { void runPreview(); }} disabled={busy || !overridesValid}>
          {busy && !preview ? <Icons.Spinner /> : <Icons.Eye />} Preview
        </button>
        <button
          className="btn btn-danger"
          onClick={() => { void runPrune(); }}
          disabled={busy || !preview || (preview.wouldDelete ?? 0) === 0}
          title={preview ? undefined : 'Preview first'}
        >
          <Icons.Trash /> Delete {preview?.wouldDelete ?? 0} run{(preview?.wouldDelete ?? 0) === 1 ? '' : 's'}
        </button>
      </div>

      {error && <div className="host-error" role="alert"><Icons.Warning /> {error}</div>}

      {preview && (
        <div className="host-prune-result" data-testid="prune-preview">
          <PolicyLine result={preview} />
          {preview.wouldDelete === 0 ? (
            <p>Nothing to delete: every run is within the policy (favorites are always kept).</p>
          ) : (
            <>
              <p><strong>{preview.wouldDelete}</strong> run{preview.wouldDelete === 1 ? '' : 's'} would be deleted:</p>
              <RunList runs={preview.runs} />
            </>
          )}
        </div>
      )}

      {result && (
        <div className="host-prune-result" data-testid="prune-result">
          <PolicyLine result={result} />
          <p>
            Deleted <strong>{result.deleted ?? 0}</strong> run{(result.deleted ?? 0) === 1 ? '' : 's'}
            {(result.failed ?? 0) > 0 && <> - <strong className="text-danger">{result.failed}</strong> could not be deleted (their folders are still on disk; see the host log)</>}.
          </p>
          {result.runs.length > 0 && <RunList runs={result.runs} />}
        </div>
      )}
    </div>
  );
}

function PolicyLine({ result }: { result: PruneResult }): React.JSX.Element {
  const rules: string[] = [];
  if (result.policy.maxRunsPerOrchestration) rules.push(`keep newest ${result.policy.maxRunsPerOrchestration} per orchestration`);
  if (result.policy.maxRunAgeDays) rules.push(`delete older than ${result.policy.maxRunAgeDays} days`);
  return (
    <p className="text-muted" data-testid="prune-policy">
      Policy ({result.policy.source}): {rules.length > 0 ? rules.join('; ') : 'none'}
    </p>
  );
}

function RunList({ runs }: { runs: PruneResult['runs'] }): React.JSX.Element {
  return (
    <ul className="host-prune-runs">
      {runs.slice(0, 50).map(run => (
        <li key={run.runId}>
          <span className="host-prune-orch">{run.orchestrationName}</span>{' '}
          <code>{run.runId}</code>{' '}
          <span className="text-muted">{run.status} - {new Date(run.startedAt).toLocaleString()}</span>
        </li>
      ))}
      {runs.length > 50 && <li className="text-muted">...and {runs.length - 50} more</li>}
    </ul>
  );
}
