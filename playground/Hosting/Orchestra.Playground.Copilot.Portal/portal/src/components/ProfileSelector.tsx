import React, { useState, useRef, useEffect, useCallback } from 'react';
import { Icons } from '../icons';
import { formatTimeAgo, formatTimeUntil } from '../utils';
import { api } from '../api';
import type { Profile } from '../types';

interface ProfileSelectorProps {
  profiles: Profile[];
  /** Currently selected profile IDs for filtering the main view */
  selectedProfileIds: string[];
  onToggleProfile: (profileId: string) => void;
  onClearFilter: () => void;
  /** Called after a profile is activated or deactivated so the parent can reload */
  onProfileChanged: () => void;
  /** Opens the full profile management modal */
  onManageProfiles: () => void;
}

/**
 * Profile selector dropdown for the main navigation bar.
 * Shows active profiles with status indicators, schedule metadata,
 * quick activate/deactivate toggles, and filter selection.
 */
export default function ProfileSelector({
  profiles,
  selectedProfileIds,
  onToggleProfile,
  onClearFilter,
  onProfileChanged,
  onManageProfiles,
}: ProfileSelectorProps): React.JSX.Element {
  const [isOpen, setIsOpen] = useState(false);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const [togglingId, setTogglingId] = useState<string | null>(null);

  // Close dropdown on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (isOpen && dropdownRef.current && !dropdownRef.current.contains(e.target as Node)) {
        setIsOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [isOpen]);

  // Close on Escape
  useEffect(() => {
    if (!isOpen) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setIsOpen(false);
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [isOpen]);

  const activeProfiles = profiles.filter(p => p.isActive);
  const inactiveProfiles = profiles.filter(p => !p.isActive);

  const handleToggleActive = useCallback(async (profile: Profile, e: React.MouseEvent) => {
    e.stopPropagation();
    setTogglingId(profile.id);
    try {
      if (profile.isActive) {
        await api.post(`/api/profiles/${profile.id}/deactivate`);
      } else {
        await api.post(`/api/profiles/${profile.id}/activate`);
      }
      onProfileChanged();
    } catch (err) {
      console.error('Failed to toggle profile:', err);
    } finally {
      setTogglingId(null);
    }
  }, [onProfileChanged]);

  /** Build the trigger label shown in the dropdown */
  const getStatusLabel = (profile: Profile): string => {
    if (!profile.isActive) {
      if (profile.nextScheduledTransition && profile.nextTransitionType === 'activation') {
        return `Activates ${formatTimeUntil(profile.nextScheduledTransition)}`;
      }
      if (profile.deactivatedAt) {
        return `Last active ${formatTimeAgo(profile.deactivatedAt)}`;
      }
      return 'Inactive';
    }
    // Active
    if (profile.activationTrigger === 'manual') {
      return `Active (manual${profile.activatedAt ? `, since ${formatTimeAgo(profile.activatedAt)}` : ''})`;
    }
    if (profile.activationTrigger === 'schedule') {
      if (profile.nextScheduledTransition && profile.nextTransitionType === 'deactivation') {
        return `Active (scheduled, ends ${formatTimeUntil(profile.nextScheduledTransition)})`;
      }
      return `Active (scheduled${profile.activatedAt ? `, since ${formatTimeAgo(profile.activatedAt)}` : ''})`;
    }
    return `Active${profile.activatedAt ? ` since ${formatTimeAgo(profile.activatedAt)}` : ''}`;
  };

  // Summary text for the trigger button
  const triggerText = (() => {
    if (selectedProfileIds.length > 0) {
      return `${selectedProfileIds.length} profile${selectedProfileIds.length > 1 ? 's' : ''}`;
    }
    const activeCount = activeProfiles.length;
    if (activeCount === 0) return 'No active profiles';
    if (activeCount === 1) return activeProfiles[0].name;
    return `${activeCount} active`;
  })();

  return (
    <div className="profile-selector" ref={dropdownRef}>
      <button
        className={`profile-selector-trigger ${selectedProfileIds.length > 0 ? 'has-filter' : ''} ${activeProfiles.length > 0 ? 'has-active' : ''}`}
        onClick={() => setIsOpen(!isOpen)}
        aria-label="Profile selector"
        aria-expanded={isOpen}
      >
        <Icons.Shield />
        <span className="profile-selector-text">{triggerText}</span>
        {activeProfiles.length > 0 && (
          <span className="profile-selector-active-dot" />
        )}
        <span className="profile-selector-caret">{isOpen ? '\u25B2' : '\u25BC'}</span>
      </button>

      {isOpen && (
        <div className="profile-selector-dropdown">
          {/* Active profiles section */}
          {activeProfiles.length > 0 && (
            <div className="profile-selector-section">
              <div className="profile-selector-section-header">Active</div>
              {activeProfiles.map(p => (
                <div
                  key={p.id}
                  className={`profile-selector-item ${selectedProfileIds.includes(p.id) ? 'selected' : ''}`}
                  onClick={() => onToggleProfile(p.id)}
                >
                  <div className="profile-selector-item-main">
                    <input
                      type="checkbox"
                      checked={selectedProfileIds.includes(p.id)}
                      onChange={() => onToggleProfile(p.id)}
                      onClick={e => e.stopPropagation()}
                    />
                    <span className="status-dot enabled" />
                    <div className="profile-selector-item-info">
                      <span className="profile-selector-item-name">{p.name}</span>
                      <span className="profile-selector-item-status">{getStatusLabel(p)}</span>
                    </div>
                    {p.matchedOrchestrationCount != null && (
                      <span className="profile-selector-item-count" title="Matched orchestrations">
                        {p.matchedOrchestrationCount}
                      </span>
                    )}
                    {p.filter.tags?.includes('*') && (
                      <span className="tag-chip tag-wildcard tag-chip-small">all</span>
                    )}
                  </div>
                  <button
                    className="profile-selector-toggle-btn deactivate"
                    onClick={(e) => handleToggleActive(p, e)}
                    disabled={togglingId === p.id}
                    title="Deactivate profile"
                  >
                    {togglingId === p.id ? <Icons.Spinner /> : <Icons.Power />}
                  </button>
                </div>
              ))}
            </div>
          )}

          {/* Inactive profiles section */}
          {inactiveProfiles.length > 0 && (
            <div className="profile-selector-section">
              <div className="profile-selector-section-header">Inactive</div>
              {inactiveProfiles.map(p => (
                <div
                  key={p.id}
                  className={`profile-selector-item inactive ${selectedProfileIds.includes(p.id) ? 'selected' : ''}`}
                  onClick={() => onToggleProfile(p.id)}
                >
                  <div className="profile-selector-item-main">
                    <input
                      type="checkbox"
                      checked={selectedProfileIds.includes(p.id)}
                      onChange={() => onToggleProfile(p.id)}
                      onClick={e => e.stopPropagation()}
                    />
                    <span className="status-dot disabled" />
                    <div className="profile-selector-item-info">
                      <span className="profile-selector-item-name">{p.name}</span>
                      <span className="profile-selector-item-status">{getStatusLabel(p)}</span>
                    </div>
                    {p.matchedOrchestrationCount != null && (
                      <span className="profile-selector-item-count" title="Matched orchestrations">
                        {p.matchedOrchestrationCount}
                      </span>
                    )}
                  </div>
                  <button
                    className="profile-selector-toggle-btn activate"
                    onClick={(e) => handleToggleActive(p, e)}
                    disabled={togglingId === p.id}
                    title="Activate profile"
                  >
                    {togglingId === p.id ? <Icons.Spinner /> : <Icons.Power />}
                  </button>
                </div>
              ))}
            </div>
          )}

          {/* Footer actions */}
          <div className="profile-selector-footer">
            {selectedProfileIds.length > 0 && (
              <button
                className="profile-selector-clear-btn"
                onClick={() => { onClearFilter(); }}
              >
                Clear filter
              </button>
            )}
            <button
              className="profile-selector-manage-btn"
              onClick={() => { onManageProfiles(); setIsOpen(false); }}
            >
              <Icons.Shield /> Manage Profiles
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
