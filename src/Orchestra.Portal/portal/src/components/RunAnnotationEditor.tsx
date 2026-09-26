import React, { useEffect, useState } from 'react';
import { Icons } from '../icons';
import { api } from '../api';
import type { RunAnnotation, TagCount } from '../types';

export interface RunAnnotationEditorProps {
  orchestrationName: string;
  runId: string;
  /** Notified after every successful mutation so parents can refresh their lists. */
  onChanged?: (annotation: RunAnnotation | null) => void;
}

/**
 * Editor for a run's curation: favorite, title, tags and note.
 *
 * Run records are immutable, so this writes to the separate annotation store via
 * `/api/history/{name}/{runId}/annotation`. Every write is a PATCH, so editing the
 * title can never silently clear the tags or note.
 *
 * The title is the field that matters most: a machine-named run such as
 * `ephemeral-efca835904b6-attempt-3` is impossible to find later, and search matches
 * the title, tags and note.
 */
export default function RunAnnotationEditor({
  orchestrationName,
  runId,
  onChanged,
}: RunAnnotationEditorProps): React.JSX.Element {
  const [annotation, setAnnotation] = useState<RunAnnotation | null>(null);
  const [title, setTitle] = useState('');
  const [note, setNote] = useState('');
  const [tagInput, setTagInput] = useState('');
  const [knownTags, setKnownTags] = useState<TagCount[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dirty, setDirty] = useState(false);

  const url = `/api/history/${encodeURIComponent(orchestrationName)}/${encodeURIComponent(runId)}/annotation`;

  useEffect(() => {
    let cancelled = false;

    const load = async () => {
      // A run with no annotation yields 404; that is the normal empty state, not an error.
      try {
        const existing = await api.get<RunAnnotation>(url);
        if (cancelled) return;
        setAnnotation(existing);
        setTitle(existing.title ?? '');
        setNote(existing.note ?? '');
      } catch {
        if (cancelled) return;
        setAnnotation(null);
        setTitle('');
        setNote('');
      }
      setDirty(false);

      try {
        const all = await api.get<{ tags: TagCount[] }>('/api/history/annotations');
        if (!cancelled) setKnownTags(all.tags ?? []);
      } catch {
        if (!cancelled) setKnownTags([]);
      }
    };

    void load();
    return () => { cancelled = true; };
  }, [url]);

  const apply = async (body: Record<string, unknown>) => {
    setSaving(true);
    setError(null);
    try {
      const updated = await api.patch<RunAnnotation>(url, body);
      setAnnotation(updated);
      setTitle(updated.title ?? '');
      setNote(updated.note ?? '');
      setDirty(false);
      onChanged?.(updated);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save annotation');
    } finally {
      setSaving(false);
    }
  };

  const favorite = annotation?.favorite ?? false;
  const tags = annotation?.tags ?? [];

  const toggleFavorite = () => void apply({ favorite: !favorite });
  const saveText = () => void apply({ title, note });

  const addTag = (raw: string) => {
    const tag = raw.trim().toLowerCase();
    if (!tag || tags.includes(tag)) {
      setTagInput('');
      return;
    }
    setTagInput('');
    void apply({ tags: [...tags, tag] });
  };

  const removeTag = (tag: string) => void apply({ tags: tags.filter(t => t !== tag) });

  const suggestions = knownTags.filter(t => !tags.includes(t.tag)).slice(0, 8);

  return (
    <div className="run-annotation-editor">
      <div className="run-annotation-row">
        <button
          type="button"
          className={`run-favorite-btn ${favorite ? 'is-favorite' : ''}`}
          onClick={toggleFavorite}
          disabled={saving}
          aria-pressed={favorite}
          title={favorite
            ? 'Favorited - this run is exempt from retention deletion'
            : 'Mark as favorite - keeps this run out of retention deletion'}
          aria-label={favorite ? 'Remove favorite' : 'Mark as favorite'}
        >
          <Icons.Star />
          <span>{favorite ? 'Favorited' : 'Favorite'}</span>
        </button>
      </div>

      <div className="form-group">
        <label htmlFor={`run-title-${runId}`}>Title</label>
        <input
          id={`run-title-${runId}`}
          className="tag-editor-input"
          type="text"
          placeholder="Name this run so you can find it later..."
          value={title}
          disabled={saving}
          onChange={e => { setTitle(e.target.value); setDirty(true); }}
          onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); saveText(); } }}
        />
      </div>

      <div className="form-group">
        <label htmlFor={`run-note-${runId}`}>Note</label>
        <textarea
          id={`run-note-${runId}`}
          className="tag-editor-input run-annotation-note"
          rows={3}
          placeholder="Caveats, findings, or why this run was kept..."
          value={note}
          disabled={saving}
          onChange={e => { setNote(e.target.value); setDirty(true); }}
        />
      </div>

      {dirty && (
        <div className="run-annotation-row">
          <button type="button" className="btn btn-sm btn-primary" onClick={saveText} disabled={saving}>
            {saving ? 'Saving...' : 'Save'}
          </button>
        </div>
      )}

      <div className="form-group">
        <label>Tags</label>
        <div className="run-annotation-tags">
          {tags.length === 0 && <span className="text-muted">No tags</span>}
          {tags.map(tag => (
            <span key={tag} className="tag-chip">
              <Icons.Tag />
              {tag}
              <button
                type="button"
                className="tag-chip-remove"
                onClick={() => removeTag(tag)}
                disabled={saving}
                aria-label={`Remove tag ${tag}`}
              >
                <Icons.X />
              </button>
            </span>
          ))}
        </div>

        <div className="tag-editor-input-row">
          <input
            className="tag-editor-input"
            type="text"
            placeholder="Add a tag..."
            value={tagInput}
            disabled={saving}
            onChange={e => setTagInput(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); addTag(tagInput); } }}
            aria-label="Add a tag"
          />
          <button
            type="button"
            className="btn btn-sm btn-secondary"
            onClick={() => addTag(tagInput)}
            disabled={saving || tagInput.trim().length === 0}
          >
            Add
          </button>
        </div>

        {suggestions.length > 0 && (
          <div className="tag-editor-suggestions">
            Known tags:{' '}
            {suggestions.map(t => (
              <button
                key={t.tag}
                type="button"
                className="tag-chip tag-chip-small tag-chip-clickable"
                onClick={() => addTag(t.tag)}
                disabled={saving}
              >
                {t.tag}
              </button>
            ))}
          </div>
        )}
      </div>

      {error && <div className="text-error run-annotation-error">{error}</div>}
    </div>
  );
}
