import React, { useState, useRef, useEffect, useCallback, useLayoutEffect } from 'react';
import { createPortal } from 'react-dom';
import { Icons, getOriginIcon } from '../icons';
import {
  type HistoryFilterState,
  type RunOrigin,
  type RunScopeFilter,
  type RunStatusFilterValue,
  ALL_RUN_ORIGINS,
  ALL_RUN_STATUS_FILTERS,
  RUN_ORIGIN_LABELS,
  DEFAULT_FILTER_STATE,
  isFilterStateDefault,
} from '../runFilters';

interface HistoryFilterSelectorProps {
  state: HistoryFilterState;
  onChange: (next: HistoryFilterState) => void;
  /**
   * Callback invoked when the user clicks "Show all executions" in the dropdown footer.
   * The button is omitted when this prop is not supplied.
   */
  onShowAllRequested?: () => void;
}

const SCOPE_OPTIONS: { value: RunScopeFilter; label: string; description: string }[] = [
  { value: 'all', label: 'All runs', description: 'Both top-level and child runs' },
  { value: 'roots', label: 'Top-level only', description: 'Hide runs invoked by another orchestration' },
  { value: 'children', label: 'Children only', description: 'Show only runs invoked by another orchestration' },
];

/** Pixels of breathing room between the trigger button and the dropdown edge. */
const DROPDOWN_GAP = 6;
/** Minimum pixels of viewport room reserved at the top/bottom edges. */
const VIEWPORT_MARGIN = 12;

interface DropdownPosition {
  /** Pixel from the viewport top. */
  top: number;
  /** Pixel from the viewport right edge. */
  right: number;
  /** Maximum height the dropdown is allowed to occupy (drives internal scroll). */
  maxHeight: number;
}

/**
 * Multi-section dropdown that controls scope, origin, status, and incomplete-hiding
 * filters for the sidebar's "Recent Executions" list. Modeled on ProfileSelector.
 *
 * The dropdown is rendered through a {@link createPortal} into <c>document.body</c>
 * so it is not clipped by the sidebar's <c>overflow: hidden</c> ancestors. Position
 * is computed from the trigger button's bounding rect and recomputed on resize and
 * scroll. The dropdown auto-flips above the trigger when there is more room upward
 * than downward (typical for the bottom-of-sidebar location of the trigger).
 *
 * Closes on outside-click and Escape. The component is purely controlled by props.
 */
export default function HistoryFilterSelector({
  state,
  onChange,
  onShowAllRequested,
}: HistoryFilterSelectorProps): React.JSX.Element {
  const [isOpen, setIsOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const [position, setPosition] = useState<DropdownPosition | null>(null);

  // Compute the dropdown position from the trigger's bounding rect. Prefers opening
  // downward; flips upward when there is more room above. Caps max-height to the
  // chosen direction's available space minus a small viewport margin so the internal
  // scroll engages instead of the content escaping the viewport.
  const computePosition = useCallback((): DropdownPosition | null => {
    const trigger = triggerRef.current;
    if (!trigger) return null;

    const rect = trigger.getBoundingClientRect();
    const viewportHeight = window.innerHeight;
    const viewportWidth = window.innerWidth;
    const spaceBelow = viewportHeight - rect.bottom - VIEWPORT_MARGIN;
    const spaceAbove = rect.top - VIEWPORT_MARGIN;

    const openUpward = spaceBelow < 280 && spaceAbove > spaceBelow;
    // Floor is generous (240px) so the scroll wrapper still has comfortable room
    // after the non-shrinking footer claims its share of the available height.
    const maxHeight = Math.max(240, openUpward ? spaceAbove - DROPDOWN_GAP : spaceBelow - DROPDOWN_GAP);
    const top = openUpward
      ? Math.max(VIEWPORT_MARGIN, rect.top - DROPDOWN_GAP - maxHeight)
      : rect.bottom + DROPDOWN_GAP;
    const right = Math.max(VIEWPORT_MARGIN, viewportWidth - rect.right);

    return { top, right, maxHeight };
  }, []);

  // Position synchronously before paint to avoid a one-frame flash at (0,0).
  useLayoutEffect(() => {
    if (!isOpen) return;
    setPosition(computePosition());
  }, [isOpen, computePosition]);

  // Re-position on resize/scroll while open so the dropdown sticks to the trigger.
  useEffect(() => {
    if (!isOpen) return;
    const update = () => setPosition(computePosition());
    window.addEventListener('resize', update);
    window.addEventListener('scroll', update, true);
    return () => {
      window.removeEventListener('resize', update);
      window.removeEventListener('scroll', update, true);
    };
  }, [isOpen, computePosition]);

  // Close on outside click. Both the trigger and the portal-rendered dropdown count as "inside".
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (!isOpen) return;
      const target = e.target as Node | null;
      if (!target) return;
      if (triggerRef.current?.contains(target)) return;
      if (dropdownRef.current?.contains(target)) return;
      setIsOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [isOpen]);

  // Close on Escape.
  useEffect(() => {
    if (!isOpen) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setIsOpen(false);
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [isOpen]);

  const setScope = useCallback((scope: RunScopeFilter) => {
    onChange({ ...state, scope });
  }, [state, onChange]);

  const toggleOrigin = useCallback((origin: RunOrigin) => {
    const next = state.origins.includes(origin)
      ? state.origins.filter(o => o !== origin)
      : [...state.origins, origin];
    onChange({ ...state, origins: next });
  }, [state, onChange]);

  const toggleStatus = useCallback((status: RunStatusFilterValue) => {
    const next = state.statuses.includes(status)
      ? state.statuses.filter(s => s !== status)
      : [...state.statuses, status];
    onChange({ ...state, statuses: next });
  }, [state, onChange]);

  const toggleHideIncomplete = useCallback(() => {
    onChange({ ...state, hideIncomplete: !state.hideIncomplete });
  }, [state, onChange]);

  const toggleFavoritesOnly = useCallback(() => {
    onChange({ ...state, favoritesOnly: !state.favoritesOnly });
  }, [state, onChange]);

  const removeTag = useCallback((tag: string) => {
    onChange({ ...state, tags: state.tags.filter(t => t !== tag) });
  }, [state, onChange]);

  const clearAll = useCallback(() => {
    onChange(DEFAULT_FILTER_STATE);
  }, [onChange]);

  const isDefault = isFilterStateDefault(state);

  // Trigger label (compact)
  const triggerText = (() => {
    const parts: string[] = [];
    if (state.scope === 'roots') parts.push('Top-level');
    else if (state.scope === 'children') parts.push('Children');

    if (state.origins.length === 0) parts.push('no origins');
    else if (state.origins.length < ALL_RUN_ORIGINS.length) parts.push(`${state.origins.length} origins`);

    if (state.statuses.length === 0) parts.push('no statuses');
    else if (state.statuses.length < ALL_RUN_STATUS_FILTERS.length) parts.push(`${state.statuses.length} statuses`);

    if (state.hideIncomplete) parts.push('completed');
    if (state.favoritesOnly) parts.push('favorites');
    if (state.tags.length > 0) parts.push(state.tags.length === 1 ? `#${state.tags[0]}` : `${state.tags.length} tags`);
    return parts.length === 0 ? 'All runs' : parts.join(' · ');
  })();

  const dropdown = isOpen && position && (
    <div
      ref={dropdownRef}
      className="history-filter-dropdown"
      role="dialog"
      aria-label="History filters"
      style={{
        position: 'fixed',
        top: position.top,
        right: position.right,
        maxHeight: position.maxHeight,
      }}
    >
      {/* Scrollable middle section. Wrapped so the footer below stays pinned via flex
          layout rather than relying on `position: sticky`, which can lose the race
          against sticky section-headers in the same scroll container. */}
      <div className="history-filter-scroll">
        {/* Curation. Placed first because favorites/tags are the fastest way to
            get back to a run you deliberately kept. */}
        <div className="history-filter-section">
          <div className="history-filter-section-header">Curation</div>
          <label className="history-filter-option">
            <input
              type="checkbox"
              checked={state.favoritesOnly}
              onChange={toggleFavoritesOnly}
            />
            <span>Favorites only</span>
          </label>
          {state.tags.length > 0 && (
            <div className="history-filter-tags">
              {state.tags.map(tag => (
                <span key={tag} className="tag-chip tag-chip-small">
                  {tag}
                  <button
                    type="button"
                    className="tag-chip-remove"
                    onClick={() => removeTag(tag)}
                    aria-label={`Remove tag filter ${tag}`}
                  >
                    &times;
                  </button>
                </span>
              ))}
            </div>
          )}
        </div>

        {/* Scope (radio) */}
        <div className="history-filter-section">
          <div className="history-filter-section-header">Scope</div>
          {SCOPE_OPTIONS.map(opt => (
            <label
              key={opt.value}
              className={`history-filter-item ${state.scope === opt.value ? 'selected' : ''}`}
              title={opt.description}
            >
              <input
                type="radio"
                name="history-scope"
                checked={state.scope === opt.value}
                onChange={() => setScope(opt.value)}
              />
              <span className="history-filter-item-label">{opt.label}</span>
            </label>
          ))}
        </div>

        {/* Origins (multi-select checkboxes) */}
        <div className="history-filter-section">
          <div className="history-filter-section-header">
            Origins
            <button
              type="button"
              className="history-filter-section-action"
              onClick={() => onChange({ ...state, origins: state.origins.length === ALL_RUN_ORIGINS.length ? [] : [...ALL_RUN_ORIGINS] })}
              title={state.origins.length === ALL_RUN_ORIGINS.length ? 'Clear all origins' : 'Select all origins'}
            >
              {state.origins.length === ALL_RUN_ORIGINS.length ? 'None' : 'All'}
            </button>
          </div>
          {ALL_RUN_ORIGINS.map(origin => (
            <label
              key={origin}
              className={`history-filter-item ${state.origins.includes(origin) ? 'selected' : ''}`}
            >
              <input
                type="checkbox"
                checked={state.origins.includes(origin)}
                onChange={() => toggleOrigin(origin)}
              />
              <span className="history-filter-item-icon" aria-hidden="true">{getOriginIcon(origin)}</span>
              <span className="history-filter-item-label">{RUN_ORIGIN_LABELS[origin]}</span>
            </label>
          ))}
        </div>

        {/* Statuses */}
        <div className="history-filter-section">
          <div className="history-filter-section-header">
            Statuses
            <button
              type="button"
              className="history-filter-section-action"
              onClick={() => onChange({ ...state, statuses: state.statuses.length === ALL_RUN_STATUS_FILTERS.length ? [] : [...ALL_RUN_STATUS_FILTERS] })}
              title={state.statuses.length === ALL_RUN_STATUS_FILTERS.length ? 'Clear all statuses' : 'Select all statuses'}
            >
              {state.statuses.length === ALL_RUN_STATUS_FILTERS.length ? 'None' : 'All'}
            </button>
          </div>
          {ALL_RUN_STATUS_FILTERS.map(status => (
            <label
              key={status}
              className={`history-filter-item ${state.statuses.includes(status) ? 'selected' : ''}`}
            >
              <input
                type="checkbox"
                checked={state.statuses.includes(status)}
                onChange={() => toggleStatus(status)}
              />
              <span className="history-filter-item-label">{status}</span>
            </label>
          ))}
        </div>

        {/* Misc */}
        <div className="history-filter-section">
          <div className="history-filter-section-header">Display</div>
          <label
            className={`history-filter-item ${state.hideIncomplete ? 'selected' : ''}`}
            title="Excludes runs that ended early via orchestra_complete or finished with no terminal step"
          >
            <input
              type="checkbox"
              checked={state.hideIncomplete}
              onChange={toggleHideIncomplete}
            />
            <span className="history-filter-item-label">Hide incomplete</span>
          </label>
        </div>
      </div>

      {/* Footer: non-shrinking flex item, always visible at the bottom of the dropdown. */}
      <div className="history-filter-footer">
        {!isDefault && (
          <button
            type="button"
            className="history-filter-clear-btn"
            onClick={clearAll}
          >
            Reset to defaults
          </button>
        )}
        {onShowAllRequested && (
          <button
            type="button"
            className="history-filter-show-all-btn"
            onClick={() => { onShowAllRequested(); setIsOpen(false); }}
          >
            Show all executions
          </button>
        )}
      </div>
    </div>
  );

  return (
    <div className="history-filter-selector">
      <button
        ref={triggerRef}
        className={`history-filter-trigger ${!isDefault ? 'has-filter' : ''}`}
        onClick={() => setIsOpen(!isOpen)}
        aria-label="History filters"
        aria-expanded={isOpen}
        title={isDefault ? 'No filters active' : `Active filters: ${triggerText}`}
      >
        <Icons.Filter />
        <span className="history-filter-trigger-text">{triggerText}</span>
        {!isDefault && <span className="history-filter-active-dot" />}
        <span className="history-filter-caret">{isOpen ? '\u25B2' : '\u25BC'}</span>
      </button>
      {dropdown && createPortal(dropdown, document.body)}
    </div>
  );
}

