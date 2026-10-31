import React, { useEffect, useState } from 'react';
import { describeError } from '../api';
import { workspaceRequest } from '../workspaceApi';

interface Definition { name: string; type: string; endpoint?: string; command?: string; arguments?: string[]; headers?: Record<string, string>; environment?: Record<string, string>; [key: string]: unknown }
interface Snapshot { path: string; revision: string; definitions: Definition[] }
const blank: Definition = { name: '', type: 'Remote', endpoint: '', headers: {} };
const lines = (map?: Record<string, string>) => Object.entries(map || {}).map(([k, v]) => `${k}=${v}`).join('\n');
function parsePairs(text: string): Record<string, string> {
  return Object.fromEntries(text.split('\n').filter(line => line.trim()).map(line => {
    const split = line.indexOf('='); if (split <= 0) throw new Error('Use NAME=value, one entry per line.');
    return [line.slice(0, split).trim(), line.slice(split + 1)];
  }));
}
export default function IntegrationSetupPanel(): React.JSX.Element {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [draft, setDraft] = useState<Definition>(blank);
  const [pairs, setPairs] = useState('');
  const [args, setArgs] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [editingName, setEditingName] = useState<string | null>(null);
  useEffect(() => { const controller = new AbortController(); void workspaceRequest<Snapshot>('/integrations', undefined, controller.signal).then(setSnapshot).catch(e => { if (!controller.signal.aborted) setError(describeError(e)); }); return () => controller.abort(); }, []);
  const edit = (definition: Definition) => { setEditingName(definition.name || null); setDraft({ ...definition }); setPairs(lines(definition.type.toLowerCase() === 'remote' ? definition.headers : definition.environment)); setArgs((definition.arguments || []).join('\n')); setMessage(''); };
  const save = async (event: React.FormEvent) => {
    event.preventDefault(); if (!snapshot) return;
    setBusy(true); setError(''); setMessage('');
    try {
      const definition = { ...draft };
      if (draft.type === 'Local') { definition.arguments = args.split('\n').filter(a => a.length > 0); definition.environment = parsePairs(pairs); delete definition.endpoint; delete definition.headers; }
      else { definition.headers = parsePairs(pairs); delete definition.command; delete definition.arguments; delete definition.environment; }
      const saved = await workspaceRequest<Snapshot>('/integrations', { revision: snapshot.revision, definition });
      setSnapshot(saved);
      const clean = saved.definitions.find(d => d.name === definition.name);
      if (clean) edit({ ...clean, type: clean.type.toLowerCase() === 'local' ? 'Local' : 'Remote' });
      setMessage('Connection saved. Test it here; restart Portal to activate the updated global MCP configuration.');
    } catch (e) { setError(describeError(e)); } finally { setBusy(false); }
  };
  const test = async (name: string) => {
    setBusy(true); setError(''); setMessage('Connecting and listing tools…');
    try { const result = await workspaceRequest<{ toolCount: number }>(`/integrations/${encodeURIComponent(name)}/test`, {}); setMessage(`Connected: ${result.toolCount} tools available. Restart Portal to activate saved configuration changes.`); }
    catch (e) { setMessage(''); setError(describeError(e)); } finally { setBusy(false); }
  };
  const remove = async () => {
    if (!snapshot || !editingName) return;
    setBusy(true); setError('');
    try {
      setSnapshot(await workspaceRequest<Snapshot>(`/integrations/${encodeURIComponent(editingName)}/remove`, { revision: snapshot.revision }));
      edit(blank); setMessage('Connection removed from the sidecar. Restart Portal to apply the change.');
    } catch (e) { setError(describeError(e)); } finally { setBusy(false); }
  };
  return <section>
    <h2>MCP connections</h2><p className="text-muted">Optional tools for your workflows. Basic authoring works without an MCP connection.</p>
    {snapshot && <p className="workspace-path">Saved in <code>{snapshot.path}</code></p>}
    {error && <div role="alert" className="workspace-error">{error}</div>}
    {message && <p role="status">{message}</p>}
    <div className="workspace-actions"><button className="btn" onClick={() => edit(blank)}>New connection</button>
      {snapshot?.definitions.map(d => <span className="workspace-connection" key={d.name}><button className="btn" onClick={() => edit({ ...d, type: d.type.toLowerCase() === 'local' ? 'Local' : 'Remote' })}>{d.name}</button><button className="btn" disabled={busy} onClick={() => { void test(d.name); }} aria-label={`Test ${d.name}`}>Test</button></span>)}
    </div>
    <form onSubmit={e => { void save(e); }}>
      <label className="workspace-field">Connection name<input className="form-input" value={draft.name} disabled={editingName !== null} onChange={e => setDraft(d => ({ ...d, name: e.target.value }))} required /></label>
      <label className="workspace-field">Transport<select className="form-input" value={draft.type} onChange={e => { setDraft({ name: draft.name, type: e.target.value }); setPairs(''); }}><option>Remote</option><option>Local</option></select></label>
      {draft.type === 'Remote'
        ? <label className="workspace-field">MCP endpoint<input className="form-input" type="url" required value={draft.endpoint || ''} onChange={e => setDraft(d => ({ ...d, endpoint: e.target.value }))} placeholder="https://example.com/mcp" /></label>
        : <><label className="workspace-field">Command<input className="form-input" required value={draft.command || ''} onChange={e => setDraft(d => ({ ...d, command: e.target.value }))} /></label>
          <label className="workspace-field">Arguments (one per line)<textarea className="form-input" rows={3} value={args} onChange={e => setArgs(e.target.value)} /></label></>}
      <label className="workspace-field">{draft.type === 'Remote' ? 'Headers' : 'Environment'} (NAME=value, one per line)
        <textarea className="form-input" rows={3} value={pairs} onChange={e => setPairs(e.target.value)} placeholder={draft.type === 'Remote' ? 'Authorization=Bearer ${API_TOKEN}' : 'API_TOKEN=${API_TOKEN}'} />
      </label>
      <p className="text-muted">Environment references are resolved on the host. Existing redacted values are retained when you save.</p>
      <button className="btn btn-primary" disabled={busy || !snapshot} type="submit">Save connection</button>
      {editingName && <button className="btn" disabled={busy} type="button" onClick={() => { void remove(); }}>Remove connection</button>}
    </form>
  </section>;
}
