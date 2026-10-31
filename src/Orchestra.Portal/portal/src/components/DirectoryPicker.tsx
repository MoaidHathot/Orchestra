import React, { useEffect, useId, useState } from 'react';
import { describeError, toApiError } from '../api';

interface Listing { currentDirectory: string; roots?: string[]; entries: { name: string; path: string; isDirectory: boolean }[] }

/** Browses the host's filesystem consistently on desktop, remote, Windows and Unix hosts. */
export default function DirectoryPicker({ initialPath, onSelect, onCancel }: {
  initialPath?: string; onSelect: (path: string) => void; onCancel: () => void;
}): React.JSX.Element {
  const [path, setPath] = useState(initialPath || '');
  const [enteredPath, setEnteredPath] = useState(initialPath || '');
  const [revision, setRevision] = useState(0);
  const inputId = useId();
  const [listing, setListing] = useState<Listing | null>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    const controller = new AbortController();
    setListing(null);
    setError('');
    const url = `/api/browse?directory=${encodeURIComponent(path)}`;
    void fetch(url, { signal: controller.signal }).then(async response => {
      if (!response.ok) throw await toApiError(response, url);
      const data = await response.json() as Listing;
      if (!controller.signal.aborted) { setListing(data); setEnteredPath(data.currentDirectory); }
    }).catch(err => { if (!controller.signal.aborted) setError(describeError(err)); });
    return () => controller.abort();
  }, [path, revision]);
  const go = () => { setPath(enteredPath); setRevision(value => value + 1); };
  return <section className="directory-picker" aria-label="Choose a folder">
    <p className="text-muted">Folders on the Orchestra host</p>
    <label htmlFor={inputId}>Folder path</label>
    <div style={{ display: 'flex', gap: 8 }}>
      <input id={inputId} className="form-input" value={enteredPath} onChange={event => setEnteredPath(event.target.value)} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); go(); } }} />
      <button type="button" className="btn" onClick={go}>Go</button>
    </div>
    {error && <p role="alert">{error}</p>}
    {listing ? <>
      <code style={{ overflowWrap: 'anywhere' }}>{listing.currentDirectory}</code>
      {listing.roots && listing.roots.length > 1 && <div className="workspace-actions">{listing.roots.map(root => <button type="button" className="btn" key={root} onClick={() => setPath(root)}>{root}</button>)}</div>}
      <div style={{ maxHeight: 220, overflow: 'auto', margin: '8px 0' }}>
        {listing.entries.filter(entry => entry.isDirectory).map(entry =>
          <button type="button" className="btn" key={entry.path} onClick={() => setPath(entry.path)} style={{ display: 'block', width: '100%', textAlign: 'left' }}>{entry.name}</button>)}
      </div>
      <button type="button" className="btn btn-primary" onClick={() => onSelect(listing.currentDirectory)}>Use this folder</button>
    </> : !error && <p role="status">Loading folders…</p>}
    <button type="button" className="btn" onClick={onCancel}>Cancel folder selection</button>
  </section>;
}
