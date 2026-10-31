import React, { useCallback, useEffect, useState } from 'react';
import { describeError } from '../api';
import { workspaceRequest, type ProviderReadiness, type ProviderSetupState, type SettingsSnapshot, type SetupJob } from '../workspaceApi';

export default function ProviderSetupPanel({ settings, onSaved }: { settings: SettingsSnapshot; onSaved: (settings: SettingsSnapshot) => void }): React.JSX.Element {
  const [providers, setProviders] = useState<ProviderReadiness[]>([]);
  const [provider, setProvider] = useState(String(settings.effective.defaultProvider || 'copilot'));
  const [model, setModel] = useState(String(settings.effective.defaultModel || ''));
  const [job, setJob] = useState<SetupJob | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [integrationId, setIntegrationId] = useState('');
  const [methodId, setMethodId] = useState('');
  const [key, setKey] = useState('');
  const [code, setCode] = useState('');
  const [answer, setAnswer] = useState<Record<string, unknown>>({});
  const current = providers.find(p => p.provider === provider);
  const integration = current?.integrations.find(i => i.id === integrationId);
  const methods = integration?.methods.filter(m => ['oauth', 'key', 'external'].includes(m.type)) ?? [];
  const method = methods.find(m => m.id === methodId);
  const running = job?.state === 'running';
  const reload = useCallback(async (signal?: AbortSignal) => {
    const result = await workspaceRequest<ProviderSetupState>('/providers', undefined, signal);
    if (signal?.aborted) return;
    setProviders(result.providers);
    const active = result.jobs.find(j => j.provider === provider && j.state === 'running');
    if (active) setJob(active);
  }, [provider]);
  useEffect(() => {
    const controller = new AbortController();
    setJob(null); setError(''); setIntegrationId(''); setMethodId(''); setKey('');
    void reload(controller.signal).catch(e => { if (!controller.signal.aborted) setError(describeError(e)); });
    return () => controller.abort();
  }, [reload]);
  useEffect(() => {
    if (!running || !job) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      try {
        const next = await workspaceRequest<SetupJob>(`/jobs/${job.id}`, undefined, controller.signal);
        if (controller.signal.aborted) return;
        setJob(next);
        if (next.result) {
          setProviders(list => list.map(p => p.provider === next.provider ? next.result! : p));
          setModel(previous => next.result!.models.some(m => m.id === previous) ? previous : next.result!.models[0]?.id || previous);
        }
        if (next.state === 'running') timer = setTimeout(() => { void poll(); }, 800);
      } catch (e) { if (!controller.signal.aborted) setError(describeError(e)); }
    };
    void poll();
    return () => { controller.abort(); clearTimeout(timer); };
  }, [job?.id, running]);
  useEffect(() => {
    setKey(''); setCode('');
    setAnswer(Object.fromEntries((method?.form ?? []).filter(f => f.default !== undefined).map(f => [f.key, f.default])));
  }, [method]);
  const operate = async (action: string) => {
    setBusy(true); setError('');
    try { setJob(await workspaceRequest<SetupJob>(`/providers/${provider}/operations`, { action, integrationId, methodId, key: key || null, answer, model })); setKey(''); }
    catch (e) { setError(describeError(e)); }
    finally { setBusy(false); }
  };
  const saveDefaults = async () => {
    setBusy(true); setError('');
    try { onSaved(await workspaceRequest<SettingsSnapshot>('/settings', { revision: settings.revision, changes: { defaultProvider: provider, defaultModel: model } })); }
    catch (e) { setError(describeError(e)); }
    finally { setBusy(false); }
  };
  const link = job?.progress.authorizationUrl;
  const safeLink = link && /^https?:\/\//i.test(link) ? link : undefined;
  return <section>
    <h2>Connect an agent</h2>
    <p className="text-muted">Sign in with your provider, choose an available model, then create workflows. Your provider manages its credentials.</p>
    {error && <div role="alert" className="workspace-error">{error}</div>}
    <label className="form-label" htmlFor="setup-provider">Agent provider</label>
    <select id="setup-provider" className="form-input" value={provider} onChange={e => setProvider(e.target.value)} disabled={busy}>
      <option value="copilot">GitHub Copilot</option><option value="opencode">OpenCode</option>
    </select>
    <p role="status">{current?.detail || 'Checking installed runtimes…'}</p>
    <div className="workspace-actions">
      {!current?.installed && <button className="btn btn-primary" disabled={busy || running} onClick={() => { void operate('install'); }}>Install runtime</button>}
      <button className="btn" disabled={busy || running} onClick={() => { void operate('check'); }}>Check connection / refresh models</button>
      {provider === 'copilot' && <button className="btn" disabled={busy || running} onClick={() => { void operate('login'); }}>Sign in to Copilot</button>}
    </div>
    {provider === 'opencode' && current?.installed && <div className="workspace-card">
      <label className="form-label" htmlFor="setup-integration">Model integration</label>
      <select id="setup-integration" className="form-input" value={integrationId} onChange={e => { setIntegrationId(e.target.value); setMethodId(''); }}>
        <option value="">Choose an integration after refreshing models</option>
        {current.integrations.map(i => <option key={i.id} value={i.id}>{i.name}{i.connected ? ' · connected' : ''}</option>)}
      </select>
      <label className="form-label" htmlFor="setup-method">Sign-in method</label>
      <select id="setup-method" className="form-input" value={methodId} onChange={e => setMethodId(e.target.value)}>
        <option value="">Choose a method</option>{methods.map(m => <option value={m.id} key={m.id}>{m.label}</option>)}
      </select>
      {(method?.form ?? []).filter(f => !f.hidden).map(field => <label className="workspace-field" key={field.key}>
        {field.title || field.key}
        {field.type === 'boolean'
          ? <input type="checkbox" checked={Boolean(answer[field.key])} onChange={e => setAnswer(a => ({ ...a, [field.key]: e.target.checked }))} />
          : field.options?.length
            ? <select className="form-input" value={String(answer[field.key] ?? '')} onChange={e => setAnswer(a => ({ ...a, [field.key]: e.target.value }))}><option value="">Select…</option>{field.options.map(o => <option key={o.value} value={o.value}>{o.label || o.value}</option>)}</select>
            : <input className="form-input" required={field.required} value={String(answer[field.key] ?? '')} onChange={e => setAnswer(a => ({ ...a, [field.key]: field.type === 'number' || field.type === 'integer' ? Number(e.target.value) : e.target.value }))} />}
        {field.description && <small className="text-muted">{field.description}</small>}
      </label>)}
      {method?.type === 'key' && <label className="workspace-field">API key<input className="form-input" type="password" autoComplete="off" value={key} onChange={e => setKey(e.target.value)} /></label>}
      <button className="btn" disabled={!methodId || busy || running} onClick={() => { void operate('login'); }}>Connect integration</button>
    </div>}
    {job && <div className={`workspace-card ${job.state === 'failed' ? 'workspace-error' : ''}`} aria-live="polite">
      <strong>{job.state === 'running' ? 'Setup in progress' : job.state}</strong><p>{job.progress.message}</p>
      {safeLink && <a className="btn" href={safeLink} target="_blank" rel="noreferrer">Open sign-in page</a>}
      {job.progress.userCode && <p>Device code: <strong><code>{job.progress.userCode}</code></strong></p>}
      {job.progress.requiresCode && running && <form onSubmit={e => { e.preventDefault(); void workspaceRequest(`/jobs/${job.id}/code`, { code }).catch(err => setError(describeError(err))); }}>
        <label className="workspace-field">Authorization code<input className="form-input" value={code} onChange={e => setCode(e.target.value)} required /></label>
        <button className="btn" type="submit">Submit code</button>
      </form>}
      {running && <button className="btn" onClick={() => { void workspaceRequest(`/jobs/${job.id}/cancel`, {}).catch(e => setError(describeError(e))); }}>Cancel setup</button>}
    </div>}
    <label className="form-label" htmlFor="setup-model">Default model</label>
    <input id="setup-model" className="form-input" list="setup-models" value={model} onChange={e => setModel(e.target.value)} placeholder="Check connection to discover models" />
    <datalist id="setup-models">{current?.models.map(m => <option key={m.id} value={m.id}>{m.name}</option>)}</datalist>
    <div className="workspace-actions">
      <button className="btn" disabled={busy || running || !model.trim() || !current?.installed} onClick={() => { void operate('test'); }}>Test selected model</button>
      <button className="btn btn-primary" disabled={busy || !model.trim()} onClick={() => { void saveDefaults(); }}>Use provider and model</button>
    </div>
  </section>;
}
