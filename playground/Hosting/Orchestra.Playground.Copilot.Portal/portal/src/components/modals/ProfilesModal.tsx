import React, { useState, useEffect, useCallback, useMemo } from 'react';
import { Icons } from '../../icons';
import { api } from '../../api';
import { useFocusTrap } from '../../hooks/useFocusTrap';
import type { Profile, ProfileFilter, ProfileSchedule, ScheduleWindow, TagCount } from '../../types';
import ImportProfilesModal from './ImportProfilesModal';
import ExportModal from './ExportModal';

// ── API response shapes ──

interface ProfilesResponse {
  count: number;
  profiles: Profile[];
}

interface TagsResponse {
  count: number;
  tags: TagCount[];
}

interface ProfileHistoryResponse {
  profileId: string;
  count: number;
  history: { profileId: string; profileName: string; action: string; reason?: string; timestamp: string }[];
}

interface EffectiveSetResponse {
  count: number;
  orchestrations: { id: string; name: string; tags: string[] }[];
}

// ── Sub-views ──

type ViewMode = 'list' | 'create' | 'edit' | 'detail';

// ── Day constants for schedule editor ──

const ALL_DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as const;
const DAY_SHORTHANDS: Record<string, string[]> = {
  weekdays: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'],
  weekends: ['Saturday', 'Sunday'],
  everyday: [...ALL_DAYS],
};

// ── Props ──

interface Props {
  open: boolean;
  onClose: () => void;
}

// ── Helper: format ISO date string for display ──

function fmtDate(iso: string | undefined): string {
  if (!iso) return '-';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

export default function ProfilesModal({ open, onClose }: Props): React.JSX.Element {
  const trapRef = useFocusTrap<HTMLDivElement>(open, onClose);

  // ── Data state ──
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [tags, setTags] = useState<TagCount[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // ── View state ──
  const [view, setView] = useState<ViewMode>('list');
  const [selectedProfile, setSelectedProfile] = useState<Profile | null>(null);
  const [effectiveSet, setEffectiveSet] = useState<EffectiveSetResponse | null>(null);
  const [profileHistory, setProfileHistory] = useState<ProfileHistoryResponse | null>(null);

  // ── Form state (create / edit) ──
  const [formName, setFormName] = useState('');
  const [formDescription, setFormDescription] = useState('');
  const [formTags, setFormTags] = useState<string[]>(['*']);
  const [formOrchIds, setFormOrchIds] = useState<string[]>([]);
  const [formExcludeIds, setFormExcludeIds] = useState<string[]>([]);
  const [formScheduleEnabled, setFormScheduleEnabled] = useState(false);
  const [formWindows, setFormWindows] = useState<ScheduleWindow[]>([]);
  const [formTimezone, setFormTimezone] = useState('');
  const [formTagInput, setFormTagInput] = useState('');
  const [formOrchInput, setFormOrchInput] = useState('');
  const [formExcludeInput, setFormExcludeInput] = useState('');
  const [saving, setSaving] = useState(false);
  const [importModal, setImportModal] = useState(false);
  const [exportModal, setExportModal] = useState(false);

  // ── Load data ──

  const loadData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [profilesData, tagsData] = await Promise.all([
        api.get<ProfilesResponse>('/api/profiles'),
        api.get<TagsResponse>('/api/tags'),
      ]);
      setProfiles(profilesData.profiles || []);
      setTags(tagsData.tags || []);
    } catch (err) {
      console.error('Failed to load profiles:', err);
      setError(err instanceof Error ? err.message : 'Failed to load profiles');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (open) {
      loadData();
      setView('list');
      setSelectedProfile(null);
    }
  }, [open, loadData]);

  // Keep selectedProfile in sync with the refreshed profiles array
  // This fixes stale state after activate/deactivate/edit operations
  useEffect(() => {
    if (selectedProfile) {
      const updated = profiles.find(p => p.id === selectedProfile.id);
      if (updated) {
        setSelectedProfile(updated);
      }
    }
  }, [profiles]); // eslint-disable-line react-hooks/exhaustive-deps

  // ── Load effective set ──

  const loadEffectiveSet = useCallback(async () => {
    try {
      const data = await api.get<EffectiveSetResponse>('/api/profiles/effective');
      setEffectiveSet(data);
    } catch (err) {
      console.error('Failed to load effective set:', err);
    }
  }, []);

  useEffect(() => {
    if (open && view === 'list') {
      loadEffectiveSet();
    }
  }, [open, view, loadEffectiveSet]);

  // ── Sorted profiles: active first, then alphabetical ──

  const sortedProfiles = useMemo(() => {
    return [...profiles].sort((a, b) => {
      if (a.isActive !== b.isActive) return a.isActive ? -1 : 1;
      return a.name.localeCompare(b.name);
    });
  }, [profiles]);

  // ── Actions ──

  const activateProfile = async (id: string) => {
    try {
      await api.post(`/api/profiles/${id}/activate`);
      await loadData();
      await loadEffectiveSet();
    } catch (err) {
      console.error('Failed to activate profile:', err);
    }
  };

  const deactivateProfile = async (id: string) => {
    try {
      await api.post(`/api/profiles/${id}/deactivate`);
      await loadData();
      await loadEffectiveSet();
    } catch (err) {
      console.error('Failed to deactivate profile:', err);
    }
  };

  const deleteProfile = async (id: string) => {
    if (!confirm('Are you sure you want to delete this profile?')) return;
    try {
      await api.delete(`/api/profiles/${id}`);
      if (selectedProfile?.id === id) {
        setSelectedProfile(null);
        setView('list');
      }
      await loadData();
      await loadEffectiveSet();
    } catch (err) {
      console.error('Failed to delete profile:', err);
    }
  };

  const openDetail = async (profile: Profile) => {
    setSelectedProfile(profile);
    setView('detail');
    try {
      const history = await api.get<ProfileHistoryResponse>(`/api/profiles/${profile.id}/history`);
      setProfileHistory(history);
    } catch (err) {
      console.error('Failed to load profile history:', err);
      setProfileHistory(null);
    }
  };

  // ── Form helpers ──

  const resetForm = () => {
    setFormName('');
    setFormDescription('');
    setFormTags(['*']);
    setFormOrchIds([]);
    setFormExcludeIds([]);
    setFormScheduleEnabled(false);
    setFormWindows([]);
    setFormTimezone('');
    setFormTagInput('');
    setFormOrchInput('');
    setFormExcludeInput('');
  };

  const populateForm = (profile: Profile) => {
    setFormName(profile.name);
    setFormDescription(profile.description || '');
    setFormTags(profile.filter.tags || ['*']);
    setFormOrchIds(profile.filter.orchestrationIds || []);
    setFormExcludeIds(profile.filter.excludeOrchestrationIds || []);
    setFormScheduleEnabled(!!profile.schedule && profile.schedule.windows.length > 0);
    setFormWindows(profile.schedule?.windows || []);
    setFormTimezone(profile.schedule?.timezone || '');
    setFormTagInput('');
    setFormOrchInput('');
    setFormExcludeInput('');
  };

  const startCreate = () => {
    resetForm();
    setView('create');
  };

  const startEdit = (profile: Profile) => {
    populateForm(profile);
    setSelectedProfile(profile);
    setView('edit');
  };

  const addFormTag = () => {
    const tag = formTagInput.trim().toLowerCase();
    if (tag && !formTags.includes(tag)) {
      setFormTags([...formTags, tag]);
    }
    setFormTagInput('');
  };

  const removeFormTag = (tag: string) => {
    setFormTags(formTags.filter(t => t !== tag));
  };

  const addFormOrchId = () => {
    const id = formOrchInput.trim();
    if (id && !formOrchIds.includes(id)) {
      setFormOrchIds([...formOrchIds, id]);
    }
    setFormOrchInput('');
  };

  const removeFormOrchId = (id: string) => {
    setFormOrchIds(formOrchIds.filter(x => x !== id));
  };

  const addFormExcludeId = () => {
    const id = formExcludeInput.trim();
    if (id && !formExcludeIds.includes(id)) {
      setFormExcludeIds([...formExcludeIds, id]);
    }
    setFormExcludeInput('');
  };

  const removeFormExcludeId = (id: string) => {
    setFormExcludeIds(formExcludeIds.filter(x => x !== id));
  };

  const addWindow = () => {
    setFormWindows([...formWindows, { days: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'], startTime: '09:00', endTime: '17:00' }]);
  };

  const removeWindow = (idx: number) => {
    setFormWindows(formWindows.filter((_, i) => i !== idx));
  };

  const updateWindow = (idx: number, updates: Partial<ScheduleWindow>) => {
    setFormWindows(formWindows.map((w, i) => i === idx ? { ...w, ...updates } : w));
  };

  const toggleWindowDay = (idx: number, day: string) => {
    const w = formWindows[idx];
    const days = w.days.includes(day) ? w.days.filter(d => d !== day) : [...w.days, day];
    updateWindow(idx, { days });
  };

  const applyDayShorthand = (idx: number, shorthand: string) => {
    const days = DAY_SHORTHANDS[shorthand];
    if (days) updateWindow(idx, { days: [...days] });
  };

  // ── Submit ──

  const handleSubmit = async () => {
    if (!formName.trim()) return;
    setSaving(true);
    setError(null);

    const filter: ProfileFilter = {
      tags: formTags.length > 0 ? formTags : ['*'],
      orchestrationIds: formOrchIds,
      excludeOrchestrationIds: formExcludeIds,
    };

    const schedule: ProfileSchedule | undefined = formScheduleEnabled && formWindows.length > 0
      ? { timezone: formTimezone || undefined, windows: formWindows }
      : undefined;

    try {
      if (view === 'create') {
        await api.post('/api/profiles', {
          name: formName.trim(),
          description: formDescription.trim() || undefined,
          filter,
          schedule,
        });
      } else if (view === 'edit' && selectedProfile) {
        await api.put(`/api/profiles/${selectedProfile.id}`, {
          name: formName.trim(),
          description: formDescription.trim() || undefined,
          filter,
          schedule,
        });
      }
      await loadData();
      await loadEffectiveSet();
      setView('list');
    } catch (err) {
      console.error('Failed to save profile:', err);
      setError(err instanceof Error ? err.message : 'Failed to save profile');
    } finally {
      setSaving(false);
    }
  };

  // ── Render helpers ──

  const activeCount = profiles.filter(p => p.isActive).length;

  const renderTagChips = (tagList: string[], removable = false, onRemove?: (tag: string) => void) => (
    <div className="profile-tags">
      {tagList.map(tag => (
        <span key={tag} className={`tag-chip ${tag === '*' ? 'tag-wildcard' : ''}`}>
          <Icons.Tag />
          {tag === '*' ? 'all' : tag}
          {removable && onRemove && (
            <button className="tag-chip-remove" onClick={() => onRemove(tag)} aria-label={`Remove tag ${tag}`}>
              <Icons.X />
            </button>
          )}
        </span>
      ))}
    </div>
  );

  // ── Render: Schedule editor ──

  const renderScheduleEditor = () => (
    <div className="profile-schedule-editor">
      <div className="profile-form-row">
        <label className="profile-form-label">
          <input
            type="checkbox"
            checked={formScheduleEnabled}
            onChange={(e) => setFormScheduleEnabled(e.target.checked)}
          />
          {' '}Enable schedule (time-based auto activation)
        </label>
      </div>

      {formScheduleEnabled && (
        <>
          <div className="profile-form-row">
            <label className="profile-form-label">Timezone (optional)</label>
            <input
              type="text"
              className="profile-form-input"
              placeholder="e.g. America/New_York (default: local)"
              value={formTimezone}
              onChange={(e) => setFormTimezone(e.target.value)}
            />
          </div>

          {formWindows.map((w, idx) => (
            <div key={idx} className="schedule-window">
              <div className="schedule-window-header">
                <span className="text-muted" style={{ fontSize: '12px' }}>Window {idx + 1}</span>
                <button className="btn-icon" onClick={() => removeWindow(idx)} title="Remove window">
                  <Icons.X />
                </button>
              </div>

              <div className="schedule-days">
                <div className="schedule-day-shortcuts">
                  {Object.keys(DAY_SHORTHANDS).map(shorthand => (
                    <button
                      key={shorthand}
                      className="btn btn-sm"
                      onClick={() => applyDayShorthand(idx, shorthand)}
                    >
                      {shorthand}
                    </button>
                  ))}
                </div>
                <div className="schedule-day-pills">
                  {ALL_DAYS.map(day => (
                    <button
                      key={day}
                      className={`schedule-day-pill ${w.days.includes(day) ? 'active' : ''}`}
                      onClick={() => toggleWindowDay(idx, day)}
                    >
                      {day.substring(0, 3)}
                    </button>
                  ))}
                </div>
              </div>

              <div className="schedule-times">
                <div>
                  <label className="text-muted" style={{ fontSize: '11px' }}>Start</label>
                  <input
                    type="time"
                    className="profile-form-input"
                    value={w.startTime}
                    onChange={(e) => updateWindow(idx, { startTime: e.target.value })}
                  />
                </div>
                <div>
                  <label className="text-muted" style={{ fontSize: '11px' }}>End</label>
                  <input
                    type="time"
                    className="profile-form-input"
                    value={w.endTime}
                    onChange={(e) => updateWindow(idx, { endTime: e.target.value })}
                  />
                </div>
              </div>
            </div>
          ))}

          <button className="btn btn-sm" onClick={addWindow} style={{ marginTop: '8px' }}>
            <Icons.Plus /> Add Time Window
          </button>
        </>
      )}
    </div>
  );

  // ── Render: List view ──

  const renderList = () => (
    <>
      {/* Summary bar */}
      <div className="profile-summary">
        <div className="profile-summary-stats">
          <span className="profile-stat">
            <Icons.Shield /> {profiles.length} profile{profiles.length !== 1 ? 's' : ''}
          </span>
          <span className="profile-stat active">
            <Icons.Power /> {activeCount} active
          </span>
          <span className="profile-stat">
            <Icons.Steps /> {effectiveSet?.count ?? '...'} orchestrations in active set
          </span>
        </div>
        <button className="btn btn-primary btn-sm" onClick={startCreate}>
          <Icons.Plus /> New Profile
        </button>
        <button className="btn btn-sm" onClick={() => setImportModal(true)} title="Import profiles from files">
          <Icons.Upload /> Import
        </button>
        <button className="btn btn-sm" onClick={() => setExportModal(true)} title="Export profiles to files" disabled={profiles.length === 0}>
          <Icons.Download /> Export
        </button>
      </div>

      {/* Available tags summary */}
      {tags.length > 0 && (
        <div className="profile-tags-bar">
          <span className="text-muted" style={{ fontSize: '12px', marginRight: '8px' }}>Tags:</span>
          {tags.map(t => (
            <span key={t.tag} className="tag-chip tag-chip-small">
              <Icons.Tag />
              {t.tag}
              <span className="tag-chip-count">{t.count}</span>
            </span>
          ))}
        </div>
      )}

      {/* Profile cards */}
      {loading ? (
        <div className="empty-state"><div className="spinner"></div></div>
      ) : profiles.length === 0 ? (
        <div className="empty-state">
          <div className="empty-title">No Profiles</div>
          <div className="empty-text">Create a profile to control which orchestrations have their triggers active.</div>
        </div>
      ) : (
        <div className="profile-cards">
          {sortedProfiles.map(profile => (
            <div
              key={profile.id}
              className={`profile-card ${profile.isActive ? 'active' : ''}`}
              onClick={() => openDetail(profile)}
              role="button"
              tabIndex={0}
              onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openDetail(profile); } }}
            >
              <div className="profile-card-header">
                <div className="profile-card-title">
                  <span className={`status-dot ${profile.isActive ? 'enabled' : 'disabled'}`}></span>
                  <span className="profile-card-name">{profile.name}</span>
                </div>
                <div className="profile-card-actions" onClick={(e) => e.stopPropagation()}>
                  <button
                    className={`btn-icon btn-toggle ${profile.isActive ? 'enabled' : ''}`}
                    onClick={() => profile.isActive ? deactivateProfile(profile.id) : activateProfile(profile.id)}
                    title={profile.isActive ? 'Deactivate' : 'Activate'}
                    aria-label={profile.isActive ? `Deactivate ${profile.name}` : `Activate ${profile.name}`}
                  >
                    <Icons.Power />
                  </button>
                  <button
                    className="btn-icon"
                    onClick={() => startEdit(profile)}
                    title="Edit profile"
                    aria-label={`Edit ${profile.name}`}
                  >
                    <Icons.Pencil />
                  </button>
                  <button
                    className="btn-icon btn-delete-small"
                    onClick={() => deleteProfile(profile.id)}
                    title="Delete profile"
                    aria-label={`Delete ${profile.name}`}
                  >
                    <Icons.Trash />
                  </button>
                </div>
              </div>

              {profile.description && (
                <div className="profile-card-desc">{profile.description}</div>
              )}

              <div className="profile-card-meta">
                {renderTagChips(profile.filter.tags)}
                {profile.schedule && profile.schedule.windows.length > 0 && (
                  <span className="profile-card-schedule">
                    <Icons.Calendar /> {profile.schedule.windows.length} schedule window{profile.schedule.windows.length !== 1 ? 's' : ''}
                  </span>
                )}
              </div>

              <div className="profile-card-footer">
                <span className="text-muted" style={{ fontSize: '11px' }}>
                  {profile.isActive ? `Active since ${fmtDate(profile.activatedAt)}` : `Last active ${fmtDate(profile.deactivatedAt)}`}
                </span>
              </div>
            </div>
          ))}
        </div>
      )}
    </>
  );

  // ── Render: Detail view ──

  const renderDetail = () => {
    if (!selectedProfile) return null;
    const p = selectedProfile;

    return (
      <div className="profile-detail">
        <button className="btn btn-sm" onClick={() => setView('list')} style={{ marginBottom: '16px' }}>
          &larr; Back to profiles
        </button>

        <div className="profile-detail-header">
          <div>
            <h3 style={{ margin: 0, display: 'flex', alignItems: 'center', gap: '8px' }}>
              <span className={`status-dot ${p.isActive ? 'enabled' : 'disabled'}`}></span>
              {p.name}
            </h3>
            {p.description && <div className="text-muted" style={{ marginTop: '4px' }}>{p.description}</div>}
          </div>
          <div style={{ display: 'flex', gap: '8px' }}>
            <button
              className={`btn ${p.isActive ? '' : 'btn-primary'} btn-sm`}
              onClick={() => p.isActive ? deactivateProfile(p.id) : activateProfile(p.id)}
            >
              <Icons.Power /> {p.isActive ? 'Deactivate' : 'Activate'}
            </button>
            <button className="btn btn-sm" onClick={() => startEdit(p)}>
              <Icons.Pencil /> Edit
            </button>
          </div>
        </div>

        {/* Filter section */}
        <div className="profile-detail-section">
          <div className="profile-detail-section-title">Filter</div>
          <div style={{ marginBottom: '8px' }}>
            <span className="text-muted" style={{ fontSize: '12px' }}>Tags: </span>
            {renderTagChips(p.filter.tags)}
          </div>
          {p.filter.orchestrationIds.length > 0 && (
            <div style={{ marginBottom: '8px' }}>
              <span className="text-muted" style={{ fontSize: '12px' }}>Include IDs: </span>
              {p.filter.orchestrationIds.map(id => (
                <span key={id} className="tag-chip">{id}</span>
              ))}
            </div>
          )}
          {p.filter.excludeOrchestrationIds.length > 0 && (
            <div>
              <span className="text-muted" style={{ fontSize: '12px' }}>Exclude IDs: </span>
              {p.filter.excludeOrchestrationIds.map(id => (
                <span key={id} className="tag-chip tag-exclude">{id}</span>
              ))}
            </div>
          )}
        </div>

        {/* Schedule section */}
        {p.schedule && p.schedule.windows.length > 0 && (
          <div className="profile-detail-section">
            <div className="profile-detail-section-title">
              <Icons.Calendar /> Schedule
            </div>
            {p.schedule.timezone && (
              <div className="text-muted" style={{ fontSize: '12px', marginBottom: '8px' }}>
                Timezone: {p.schedule.timezone}
              </div>
            )}
            {p.schedule.windows.map((w, idx) => (
              <div key={idx} className="schedule-window-display">
                <span className="schedule-window-days">
                  {w.days.join(', ')}
                </span>
                <span className="schedule-window-time">
                  {w.startTime} - {w.endTime}
                </span>
              </div>
            ))}
          </div>
        )}

        {/* Timestamps */}
        <div className="profile-detail-section">
          <div className="profile-detail-section-title">Info</div>
          <div className="profile-detail-grid">
            <span className="text-muted">Created:</span><span>{fmtDate(p.createdAt)}</span>
            <span className="text-muted">Updated:</span><span>{fmtDate(p.updatedAt)}</span>
            <span className="text-muted">ID:</span><span style={{ fontFamily: 'monospace', fontSize: '12px' }}>{p.id}</span>
          </div>
        </div>

        {/* History */}
        {profileHistory && profileHistory.history.length > 0 && (
          <div className="profile-detail-section">
            <div className="profile-detail-section-title">
              <Icons.History /> Activation History
            </div>
            <div className="profile-history-list">
              {profileHistory.history.slice(0, 20).map((entry, idx) => (
                <div key={idx} className="profile-history-entry">
                  <span className={`profile-history-action ${entry.action.toLowerCase()}`}>
                    {entry.action}
                  </span>
                  <span className="text-muted" style={{ fontSize: '12px' }}>
                    {fmtDate(entry.timestamp)}
                  </span>
                  {entry.reason && (
                    <span className="text-muted" style={{ fontSize: '11px' }}>
                      ({entry.reason})
                    </span>
                  )}
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
    );
  };

  // ── Render: Create / Edit form ──

  const renderForm = () => (
    <div className="profile-form">
      <button className="btn btn-sm" onClick={() => setView('list')} style={{ marginBottom: '16px' }}>
        &larr; Back to profiles
      </button>

      <h3 style={{ margin: '0 0 16px 0' }}>
        {view === 'create' ? 'Create Profile' : `Edit: ${selectedProfile?.name}`}
      </h3>

      {error && (
        <div className="profile-error">{error}</div>
      )}

      {/* Name */}
      <div className="profile-form-row">
        <label className="profile-form-label">Name *</label>
        <input
          type="text"
          className="profile-form-input"
          placeholder="e.g. On-Call, Work Hours, Vacation"
          value={formName}
          onChange={(e) => setFormName(e.target.value)}
          autoFocus
        />
      </div>

      {/* Description */}
      <div className="profile-form-row">
        <label className="profile-form-label">Description</label>
        <input
          type="text"
          className="profile-form-input"
          placeholder="Optional description"
          value={formDescription}
          onChange={(e) => setFormDescription(e.target.value)}
        />
      </div>

      {/* Tags filter */}
      <div className="profile-form-row">
        <label className="profile-form-label">Tags (orchestrations matching any tag are included)</label>
        {renderTagChips(formTags, true, removeFormTag)}
        <div className="profile-form-input-row">
          <input
            type="text"
            className="profile-form-input"
            placeholder="Add tag..."
            value={formTagInput}
            onChange={(e) => setFormTagInput(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addFormTag(); } }}
          />
          <button className="btn btn-sm" onClick={addFormTag} disabled={!formTagInput.trim()}>Add</button>
        </div>
        {/* Quick-add from known tags */}
        {tags.length > 0 && (
          <div className="profile-form-suggestions">
            <span className="text-muted" style={{ fontSize: '11px' }}>Known tags: </span>
            {tags.filter(t => !formTags.includes(t.tag)).map(t => (
              <button
                key={t.tag}
                className="tag-chip tag-chip-small tag-chip-clickable"
                onClick={() => setFormTags([...formTags, t.tag])}
              >
                <Icons.Tag />{t.tag}
              </button>
            ))}
            {!formTags.includes('*') && (
              <button
                className="tag-chip tag-chip-small tag-wildcard tag-chip-clickable"
                onClick={() => setFormTags([...formTags, '*'])}
              >
                <Icons.Tag />all (*)
              </button>
            )}
          </div>
        )}
      </div>

      {/* Explicit orchestration IDs */}
      <div className="profile-form-row">
        <label className="profile-form-label">Include Orchestration IDs (optional)</label>
        <div className="profile-tags">
          {formOrchIds.map(id => (
            <span key={id} className="tag-chip">
              {id}
              <button className="tag-chip-remove" onClick={() => removeFormOrchId(id)}>
                <Icons.X />
              </button>
            </span>
          ))}
        </div>
        <div className="profile-form-input-row">
          <input
            type="text"
            className="profile-form-input"
            placeholder="Orchestration ID..."
            value={formOrchInput}
            onChange={(e) => setFormOrchInput(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addFormOrchId(); } }}
          />
          <button className="btn btn-sm" onClick={addFormOrchId} disabled={!formOrchInput.trim()}>Add</button>
        </div>
      </div>

      {/* Exclude orchestration IDs */}
      <div className="profile-form-row">
        <label className="profile-form-label">Exclude Orchestration IDs (optional)</label>
        <div className="profile-tags">
          {formExcludeIds.map(id => (
            <span key={id} className="tag-chip tag-exclude">
              {id}
              <button className="tag-chip-remove" onClick={() => removeFormExcludeId(id)}>
                <Icons.X />
              </button>
            </span>
          ))}
        </div>
        <div className="profile-form-input-row">
          <input
            type="text"
            className="profile-form-input"
            placeholder="Orchestration ID to exclude..."
            value={formExcludeInput}
            onChange={(e) => setFormExcludeInput(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addFormExcludeId(); } }}
          />
          <button className="btn btn-sm" onClick={addFormExcludeId} disabled={!formExcludeInput.trim()}>Add</button>
        </div>
      </div>

      {/* Schedule */}
      <div className="profile-form-row">
        <label className="profile-form-label"><Icons.Calendar /> Schedule</label>
        {renderScheduleEditor()}
      </div>

      {/* Submit */}
      <div className="profile-form-actions">
        <button className="btn" onClick={() => setView('list')}>Cancel</button>
        <button
          className="btn btn-primary"
          onClick={handleSubmit}
          disabled={!formName.trim() || saving}
        >
          {saving ? 'Saving...' : (view === 'create' ? 'Create Profile' : 'Save Changes')}
        </button>
      </div>
    </div>
  );

  // ── Main render ──

  return (
    <div
      className={`modal-overlay ${open ? 'visible' : ''}`}
      ref={trapRef}
      onClick={(e: React.MouseEvent<HTMLDivElement>) => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div className="modal modal-lg" role="dialog" aria-modal="true" aria-label="Profiles & Tags">
        <div className="modal-header">
          <div className="modal-title">
            <Icons.Shield /> Profiles & Tags
          </div>
          <button className="modal-close" aria-label="Close" onClick={onClose}>
            <Icons.X />
          </button>
        </div>
        <div className="modal-body" style={{ minHeight: '400px' }}>
          {error && view === 'list' && (
            <div className="profile-error">{error}</div>
          )}
          {view === 'list' && renderList()}
          {view === 'detail' && renderDetail()}
          {(view === 'create' || view === 'edit') && renderForm()}
        </div>
        <div className="modal-footer">
          <button className="btn" onClick={onClose}>Close</button>
        </div>
      </div>

      {/* Import/Export sub-modals */}
      <ImportProfilesModal
        open={importModal}
        onClose={() => setImportModal(false)}
        onImported={() => { loadData(); loadEffectiveSet(); }}
      />
      <ExportModal
        open={exportModal}
        onClose={() => setExportModal(false)}
        title="Export Profiles"
        endpoint="/api/profiles/export"
        idsField="profileIds"
      />
    </div>
  );
}
