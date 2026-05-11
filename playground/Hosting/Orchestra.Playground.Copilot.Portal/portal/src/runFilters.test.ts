import { describe, it, expect, beforeEach } from 'vitest';
import {
  classifyRunOrigin,
  buildFilterQueryString,
  isFilterStateDefault,
  loadFilterState,
  saveFilterState,
  DEFAULT_FILTER_STATE,
  ALL_RUN_ORIGINS,
  ALL_RUN_STATUS_FILTERS,
  FILTER_STORAGE_KEY,
  type HistoryFilterState,
} from './runFilters';

describe('classifyRunOrigin', () => {
  it.each([
    ['manual', 'manual'],
    ['MANUAL', 'manual'],
    ['scheduler', 'scheduler'],
    ['loop', 'loop'],
    ['webhook', 'webhook'],
    ['mcp', 'mcp'],
    ['retry', 'retry'],
    ['resume', 'resume'],
  ] as const)('classifies %s as %s', (input, expected) => {
    expect(classifyRunOrigin(input)).toBe(expected);
  });

  it.each([
    'orchestration:my-orch:abc',
    'orchestration:abc',
    'ORCHESTRATION:UPPER',
  ])('classifies %s as orchestration', (input) => {
    expect(classifyRunOrigin(input)).toBe('orchestration');
  });

  it.each([null, undefined, '', '   ', 'garbage', 'orchestrate'])(
    'classifies %s as unknown',
    (input) => {
      expect(classifyRunOrigin(input)).toBe('unknown');
    },
  );
});

describe('buildFilterQueryString', () => {
  it('returns an empty string when state matches defaults', () => {
    expect(buildFilterQueryString(DEFAULT_FILTER_STATE)).toBe('');
  });

  it('emits ?origins= only when the allow-list is narrower than ALL', () => {
    const state: HistoryFilterState = {
      ...DEFAULT_FILTER_STATE,
      origins: ['manual', 'scheduler'],
    };
    const qs = buildFilterQueryString(state);
    expect(qs).toContain('origins=manual,scheduler');
  });

  it('does NOT emit ?origins= when the allow-list is empty (no origins)', () => {
    const state: HistoryFilterState = {
      ...DEFAULT_FILTER_STATE,
      origins: [],
    };
    const qs = buildFilterQueryString(state);
    expect(qs).not.toContain('origins=');
  });

  it('emits ?statuses= only when the allow-list is narrower than ALL', () => {
    const state: HistoryFilterState = {
      ...DEFAULT_FILTER_STATE,
      statuses: ['Failed'],
    };
    expect(buildFilterQueryString(state)).toContain('statuses=Failed');
  });

  it('emits ?roots=true for scope "roots"', () => {
    const state: HistoryFilterState = {
      ...DEFAULT_FILTER_STATE,
      scope: 'roots',
    };
    expect(buildFilterQueryString(state)).toContain('roots=true');
  });

  it('emits ?roots=false for scope "children"', () => {
    const state: HistoryFilterState = {
      ...DEFAULT_FILTER_STATE,
      scope: 'children',
    };
    expect(buildFilterQueryString(state)).toContain('roots=false');
  });

  it('does NOT emit ?roots= for scope "all"', () => {
    const state: HistoryFilterState = {
      ...DEFAULT_FILTER_STATE,
      scope: 'all',
    };
    expect(buildFilterQueryString(state)).not.toContain('roots=');
  });

  it('combines multiple narrowed filters with & separators', () => {
    const state: HistoryFilterState = {
      scope: 'roots',
      origins: ['manual'],
      statuses: ['Succeeded'],
      hideIncomplete: true,
    };
    const qs = buildFilterQueryString(state);
    expect(qs.startsWith('&')).toBe(true);
    expect(qs).toContain('origins=manual');
    expect(qs).toContain('statuses=Succeeded');
    expect(qs).toContain('roots=true');
  });

  it('does not emit hideIncomplete in the URL (it is client-side filtering)', () => {
    const state: HistoryFilterState = {
      ...DEFAULT_FILTER_STATE,
      hideIncomplete: false,
    };
    expect(buildFilterQueryString(state)).not.toContain('incomplete');
  });
});

describe('isFilterStateDefault', () => {
  it('returns true for the default state', () => {
    expect(isFilterStateDefault(DEFAULT_FILTER_STATE)).toBe(true);
  });

  it.each<Partial<HistoryFilterState>>([
    { scope: 'roots' },
    { hideIncomplete: false },
    { origins: ['manual'] },
    { statuses: ['Failed'] },
  ])('returns false when %o is changed', (changes) => {
    const state: HistoryFilterState = { ...DEFAULT_FILTER_STATE, ...changes };
    expect(isFilterStateDefault(state)).toBe(false);
  });
});

describe('loadFilterState / saveFilterState', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('returns DEFAULT_FILTER_STATE when nothing is stored', () => {
    expect(loadFilterState()).toEqual(DEFAULT_FILTER_STATE);
  });

  it('returns DEFAULT_FILTER_STATE when stored value is malformed JSON', () => {
    localStorage.setItem(FILTER_STORAGE_KEY, '{not json');
    expect(loadFilterState()).toEqual(DEFAULT_FILTER_STATE);
  });

  it('round-trips a custom state', () => {
    const state: HistoryFilterState = {
      scope: 'children',
      origins: ['manual', 'retry'],
      statuses: ['Failed', 'Cancelled'],
      hideIncomplete: false,
    };
    saveFilterState(state);

    const loaded = loadFilterState();
    expect(loaded).toEqual(state);
  });

  it('drops unknown origin tokens during deserialisation', () => {
    localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify({
      scope: 'all',
      origins: ['manual', 'garbage', 'scheduler'],
      statuses: [...ALL_RUN_STATUS_FILTERS],
      hideIncomplete: true,
    }));

    const loaded = loadFilterState();
    expect(loaded.origins).toEqual(['manual', 'scheduler']);
  });

  it('drops unknown status tokens during deserialisation', () => {
    localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify({
      scope: 'all',
      origins: [...ALL_RUN_ORIGINS],
      statuses: ['Succeeded', 'Botched'],
      hideIncomplete: true,
    }));

    const loaded = loadFilterState();
    expect(loaded.statuses).toEqual(['Succeeded']);
  });

  it('falls back to default scope when stored scope is invalid', () => {
    localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify({
      scope: 'invalid',
      origins: [...ALL_RUN_ORIGINS],
      statuses: [...ALL_RUN_STATUS_FILTERS],
      hideIncomplete: true,
    }));

    expect(loadFilterState().scope).toBe(DEFAULT_FILTER_STATE.scope);
  });
});
