import React, { useEffect, useRef, useState } from 'react';
import { describeError } from '../../api';
import { useFocusTrap } from '../../hooks/useFocusTrap';
import { workspaceRequest, type WorkspaceInfo, type SettingsSnapshot, type WorkspaceEntry } from '../../workspaceApi';
import type { Orchestration } from '../../types';
import ProviderSetupPanel from '../ProviderSetupPanel';
import IntegrationSetupPanel from '../IntegrationSetupPanel';
import DirectoryPicker from '../DirectoryPicker';
import { Icons } from '../../icons';
import '../../workspace.css';

export type WorkspaceTab = 'welcome' | 'providers' | 'workspace' | 'integrations' | 'execution' | 'history';
const tabs: { id: WorkspaceTab; label: string }[] = [
  { id: 'welcome', label: 'Get started' }, { id: 'providers', label: 'Providers' }, { id: 'workspace', label: 'Workspace' },
  { id: 'integrations', label: 'Connections' }, { id: 'execution', label: 'Execution' }, { id: 'history', label: 'History' },
];

export default function WorkspaceModal({ initialTab, onClose, onRun, onImport, onDiagnostics, onChanged }: {
  initialTab: WorkspaceTab; onClose: () => void; onRun: (orchestration: Orchestration, parameters: Record<string, string>) => void;
  onImport: () => void; onDiagnostics: () => void; onChanged: () => void;
}): React.JSX.Element {
  const [tab, setTab] = useState(initialTab);
  const [workspace, setWorkspace] = useState<WorkspaceInfo | null>(null);
  const [settings, setSettings] = useState<SettingsSnapshot | null>(null);
  const [changes, setChanges] = useState<Record<string, unknown>>({});
  const [description, setDescription] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [message, setMessage] = useState('');
  const [picking, setPicking] = useState(false);
  const alive = useRef(true);
  const trap = useFocusTrap<HTMLDivElement>(true); // Explicit close protects in-progress forms.
  const load = async (signal?: AbortSignal) => {
    const [info, snapshot] = await Promise.all([workspaceRequest<WorkspaceInfo>('', undefined, signal), workspaceRequest<SettingsSnapshot>('/settings', undefined, signal)]);
    if (signal?.aborted || !alive.current) return;
    setWorkspace(info); setSettings(snapshot); setChanges({}); setError('');
  };
  useEffect(() => {
    alive.current = true;
    const controller = new AbortController();
    void load(controller.signal).catch(e => { if (!controller.signal.aborted) setError(describeError(e)); });
    return () => { alive.current = false; controller.abort(); };
  }, []);
  const changed = Object.keys(changes).length > 0;
  const value = (key: string): unknown => Object.prototype.hasOwnProperty.call(changes, key) ? changes[key] : settings?.values[key] ?? settings?.effective[key];
  const text = (key: string) => String(value(key) ?? '');
  const nested = (key: string, field: string): unknown => {
    const patch = changes[key] as Record<string, unknown> | undefined;
    if (patch && Object.prototype.hasOwnProperty.call(patch, field)) return patch[field];
    return (settings?.values[key] as Record<string, unknown> | undefined)?.[field] ?? (settings?.effective[key] as Record<string, unknown> | undefined)?.[field];
  };
  const change = (key: string, next: unknown) => setChanges(c => ({ ...c, [key]: next }));
  const changeNested = (key: string, field: string, next: unknown) => setChanges(c => ({ ...c, [key]: { ...(c[key] as object || {}), [field]: next } }));
  const saved = (snapshot: SettingsSnapshot) => {
    setSettings(snapshot); setChanges({}); setMessage('Settings saved. Execution defaults apply to new runs.'); onChanged();
    setWorkspace(w => w ? { ...w, defaultProvider: String(snapshot.effective.defaultProvider), defaultModel: String(snapshot.effective.defaultModel) } : w);
  };
  const save = async () => {
    if (!settings) return;
    setBusy(true); setError(''); setMessage('');
    try { const snapshot = await workspaceRequest<SettingsSnapshot>('/settings', { revision: settings.revision, changes }); if (alive.current) saved(snapshot); }
    catch (e) { if (alive.current) setError(describeError(e)); }
    finally { if (alive.current) setBusy(false); }
  };
  const fetchOrchestration = async (entry: WorkspaceEntry) => {
    const response = await fetch(`/api/orchestrations/${encodeURIComponent(entry.id)}`, { cache: 'no-store' });
    if (!response.ok) throw new Error('Could not load the prepared workflow. Refresh the library.');
    return await response.json() as Orchestration;
  };
  const start = async (kind: string) => {
    setBusy(true); setError(''); setMessage('');
    try {
      const entry = await workspaceRequest<WorkspaceEntry>(kind === 'demo' ? '/templates/smoke-test' : `/assistants/${kind}`, {});
      const orchestration = await fetchOrchestration(entry);
      if (!alive.current) return;
      void workspaceRequest('/dismiss', {}).catch(() => {});
      onChanged(); onRun(orchestration, kind === 'demo' ? {} : { description });
    } catch (e) { if (alive.current) setError(describeError(e)); }
    finally { if (alive.current) setBusy(false); }
  };
  const addTemplate = async (id: string) => {
    setBusy(true); setError('');
    try { const entry = await workspaceRequest<WorkspaceEntry>(`/templates/${id}`, {}); setMessage(`${entry.name} is available in your workflow library.`); onChanged(); }
    catch (e) { setError(describeError(e)); } finally { setBusy(false); }
  };
  const close = () => { void workspaceRequest('/dismiss', {}).catch(() => {}); onClose(); };
  const textField = (label: string, key: string, placeholder?: string) => <label className="workspace-field">{label}
    <input className="form-input" value={text(key)} placeholder={placeholder} onChange={e => change(key, e.target.value || null)} />
  </label>;

  return <div className="modal-overlay visible" ref={trap}>
    <div className="modal workspace-modal" role="dialog" aria-modal="true" aria-label="Orchestra workspace">
      <header className="modal-header"><div><h1>Orchestra workspace</h1><p className="text-muted">Set up, create, and make it yours.</p></div>
        <button className="modal-close" aria-label={changed ? 'Close and discard unsaved settings' : 'Close workspace'} onClick={close}><Icons.X /></button>
      </header>
      <nav className="workspace-tabs" role="tablist" aria-label="Workspace sections">
        {tabs.map((item, index) => <button type="button" id={`workspace-tab-${item.id}`} key={item.id} role="tab" aria-selected={tab === item.id} aria-controls="workspace-tabpanel" tabIndex={tab === item.id ? 0 : -1}
          onClick={() => { setTab(item.id); setError(''); setMessage(''); }} onKeyDown={e => {
            const next = e.key === 'ArrowRight' ? (index + 1) % tabs.length : e.key === 'ArrowLeft' ? (index + tabs.length - 1) % tabs.length : e.key === 'Home' ? 0 : e.key === 'End' ? tabs.length - 1 : -1;
            if (next >= 0) { e.preventDefault(); setTab(tabs[next].id); document.getElementById(`workspace-tab-${tabs[next].id}`)?.focus(); }
          }}>{item.label}</button>)}
      </nav>
      <div className="modal-body workspace-body" role="tabpanel" id="workspace-tabpanel" aria-labelledby={`workspace-tab-${tab}`}>
        {error && <div role="alert" className="workspace-error">{error} <button className="btn" onClick={() => { void load().catch(e => setError(describeError(e))); }}>Reload</button></div>}
        {message && <p className="workspace-success" role="status">{message}</p>}
        {(!workspace || !settings) && !error && <p role="status">Loading workspace…</p>}
        {workspace && settings && <>
          {settings.error && <div role="alert" className="workspace-error">Configuration could not be parsed: {settings.error}<br />File: {settings.configPath}</div>}
          {tab === 'welcome' && <>
            <section className="workspace-hero"><h2>From an idea to a running workflow</h2><p>Start with a demo, connect an agent, then describe what you want Orchestra to do.</p>
              <div className="workspace-actions"><button className="btn btn-primary" disabled={busy} onClick={() => { void start('demo'); }}>Try the demo</button><button className="btn" onClick={() => setTab('providers')}>Connect an agent</button><button className="btn" onClick={onImport}>Import a workflow</button></div>
              <small>The demo uses your installed .NET runtime and runs without agent credentials.</small>
            </section>
            <section className="workspace-card"><h3>Create with an assistant</h3>
              <p className="text-muted">Your agent can ask clarifying questions in Portal. A deterministic check validates and saves the result into your workspace.</p>
              <label className="workspace-field">What would you like to do?<textarea className="form-input" rows={4} value={description} onChange={e => setDescription(e.target.value)} placeholder="Create a workflow that fetches a JSON API, summarizes it, and asks me to approve the result…" /></label>
              <div className="workspace-actions"><button className="btn btn-primary" disabled={busy || !description.trim()} onClick={() => { void start('create-orchestration'); }}>Create workflow</button><button className="btn" disabled={busy || !description.trim()} onClick={() => { void start('configure-orchestra'); }}>Help configure Orchestra</button></div>
              <p className="text-muted">Using {workspace.defaultProvider} · {workspace.defaultModel}. <button className="workspace-link" onClick={() => setTab('providers')}>Check or change provider</button></p>
            </section>
            <details className="workspace-card"><summary>Start from an example</summary><div className="workspace-template-grid">{workspace.templates.map(template => <article key={template.id}><h4>{template.id}</h4><p>{template.summary}</p><small>{template.requiresAgent ? 'Agent required' : 'No agent required'}{template.requiresPowerShell ? ' · PowerShell required' : ''}</small><button className="btn" disabled={busy} onClick={() => { void addTemplate(template.id); }}>Add {template.id}</button></article>)}</div></details>
            <p className="workspace-path">Workflows: <code>{workspace.workflowDirectory}</code></p>
          </>}
          {tab === 'providers' && <ProviderSetupPanel settings={settings} onSaved={saved} />}
          {tab === 'integrations' && <IntegrationSetupPanel />}
          {['workspace', 'execution', 'history'].includes(tab) && <form onSubmit={e => { e.preventDefault(); void save(); }}>
            <p className="workspace-path">Configuration: <code>{settings.configPath}</code><br /><span className="text-muted">Source: {settings.source}. Unrelated configuration and comments are preserved.</span></p>
            {Object.entries(settings.overrides).length > 0 && <div className="workspace-card">Launch overrides take precedence: {Object.entries(settings.overrides).map(([key, source]) => <p key={key}><strong>{key}</strong>: {source}</p>)}</div>}
            {tab === 'workspace' && <>
              <h2>Workspace and storage</h2><p className="text-muted">Currently saving workflows under {workspace.workflowDirectory}. Directory and listener changes take effect after a restart.</p>
              <label className="workspace-field">Workspace root (contains orchestrations/)<input className="form-input" value={String(nested('scan', 'directory') ?? '')} onChange={e => changeNested('scan', 'directory', e.target.value)} /></label>
              <button className="btn" type="button" onClick={() => setPicking(!picking)}>Choose workspace folder</button>
              {picking && <DirectoryPicker initialPath={String(nested('scan', 'directory') ?? '')} onCancel={() => setPicking(false)} onSelect={path => { changeNested('scan', 'directory', path); setPicking(false); }} />}
              <label className="workspace-check"><input type="checkbox" checked={Boolean(nested('scan', 'recursive'))} onChange={e => changeNested('scan', 'recursive', e.target.checked)} /> Include subfolders</label>
              <label className="workspace-check"><input type="checkbox" checked={Boolean(nested('scan', 'watch'))} onChange={e => changeNested('scan', 'watch', e.target.checked)} /> Watch workflow files for changes</label>
              {textField('Run history and registry directory', 'dataPath')}{textField('Portal listening URL(s)', 'urls', 'http://localhost:5000')}
            </>}
            {tab === 'execution' && <>
              <h2>Execution defaults</h2><p className="text-muted">Defaults apply to new runs. Individual workflows and steps can override them.</p>
              {textField('Default model', 'defaultModel')}
              <label className="workspace-field">Maximum concurrent steps per run<input className="form-input" type="number" min={1} value={typeof value('defaultMaxConcurrency') === 'number' ? String(value('defaultMaxConcurrency')) : ''} placeholder="Unbounded" onChange={e => change('defaultMaxConcurrency', e.target.value ? Number(e.target.value) : null)} /></label>
              <label className="workspace-field">Reasoning effort<select className="form-input" value={text('defaultReasoningLevel').toLowerCase()} onChange={e => change('defaultReasoningLevel', e.target.value || null)}><option value="">Provider default</option>{['low', 'medium', 'high', 'xhigh', 'max'].map(v => <option key={v}>{v}</option>)}</select></label>
              <label className="workspace-check"><input type="checkbox" checked={Boolean(value('enableScheduler'))} onChange={e => change('enableScheduler', e.target.checked)} /> Enable scheduled triggers (restart required)</label>
            </>}
            {tab === 'history' && <>
              <h2>History and diagnostics</h2><p className="text-muted">Retention applies during normal cleanup. Favorited runs are preserved.</p>
              <label className="workspace-field">Maximum runs per orchestration<input className="form-input" type="number" min={0} value={String(nested('retention', 'maxRunsPerOrchestration') ?? '')} placeholder="Keep all" onChange={e => changeNested('retention', 'maxRunsPerOrchestration', e.target.value ? Number(e.target.value) : null)} /></label>
              <label className="workspace-field">Maximum run age (days)<input className="form-input" type="number" min={0} value={String(nested('retention', 'maxRunAgeDays') ?? '')} placeholder="Keep all" onChange={e => changeNested('retention', 'maxRunAgeDays', e.target.value ? Number(e.target.value) : null)} /></label>
              <label className="workspace-field">Log level<select className="form-input" value={text('logLevel')} onChange={e => change('logLevel', e.target.value)}>{['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical', 'None'].map(v => <option key={v}>{v}</option>)}</select></label>
              <button type="button" className="btn" onClick={onDiagnostics}>Open health, logs and maintenance</button>
            </>}
            {settings.restartRequired.length > 0 && <p className="workspace-restart" role="status">Saved changes require a Portal restart: {settings.restartRequired.join(', ')}. Restart with your existing launch options.</p>}
            <div className="workspace-actions"><button type="submit" className="btn btn-primary" disabled={busy || !changed || Boolean(settings.error)}>Save settings</button><span className="text-muted">{changed ? 'Unsaved changes' : 'Up to date'}</span></div>
          </form>}
        </>}
      </div>
    </div>
  </div>;
}
