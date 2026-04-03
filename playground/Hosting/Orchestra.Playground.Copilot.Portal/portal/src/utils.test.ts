import { describe, it, expect } from 'vitest';
import {
  isIncompleteExecution,
  profileFilterMatchesOrchestration,
  getMatchingProfiles,
  orchestrationMatchesProfileFilter,
  orchestrationMatchesSearch,
} from './utils';
import type { Profile, ProfileFilter } from './types';

// ── isIncompleteExecution ────────────────────────────────────────────────────

describe('isIncompleteExecution', () => {
  it('returns false for a fully succeeded execution', () => {
    expect(isIncompleteExecution({ status: 'Succeeded' })).toBe(false);
  });

  it('returns false for a failed execution', () => {
    expect(isIncompleteExecution({ status: 'Failed' })).toBe(false);
  });

  it('returns false for a cancelled execution', () => {
    expect(isIncompleteExecution({ status: 'Cancelled' })).toBe(false);
  });

  it('returns true when isIncomplete flag is set', () => {
    expect(isIncompleteExecution({ status: 'Succeeded', isIncomplete: true })).toBe(true);
  });

  it('returns true when completionReason is set and status is Succeeded', () => {
    expect(
      isIncompleteExecution({
        status: 'Succeeded',
        completionReason: 'No new information available',
      }),
    ).toBe(true);
  });

  it('returns true when both isIncomplete and completionReason are set', () => {
    expect(
      isIncompleteExecution({
        status: 'Succeeded',
        isIncomplete: true,
        completionReason: 'Early exit',
      }),
    ).toBe(true);
  });

  it('returns false for a failed execution with completionReason (orchestra_complete with failed status)', () => {
    // When orchestra_complete is called with status "failed", completionReason is set but
    // isIncomplete should be true. If isIncomplete is not set, we don't flag it as incomplete
    // because a "Failed" status is meaningful on its own.
    expect(
      isIncompleteExecution({
        status: 'Failed',
        completionReason: 'Something went wrong',
      }),
    ).toBe(false);
  });

  it('returns false for an active/running execution even if isIncomplete is set', () => {
    expect(
      isIncompleteExecution({
        status: 'Succeeded',
        isActive: true,
        isIncomplete: true,
      }),
    ).toBe(false);
  });

  it('returns false for an active/running execution with completionReason', () => {
    expect(
      isIncompleteExecution({
        status: 'Succeeded',
        isActive: true,
        completionReason: 'Early exit',
      }),
    ).toBe(false);
  });

  it('returns false when no fields are set', () => {
    expect(isIncompleteExecution({})).toBe(false);
  });

  it('returns true for isIncomplete with no status', () => {
    expect(isIncompleteExecution({ isIncomplete: true })).toBe(true);
  });
});

// ── profileFilterMatchesOrchestration ────────────────────────────────────────

describe('profileFilterMatchesOrchestration', () => {
  const makeFilter = (overrides: Partial<ProfileFilter> = {}): ProfileFilter => ({
    tags: [],
    orchestrationIds: [],
    excludeOrchestrationIds: [],
    ...overrides,
  });

  it('returns false for an empty filter (no tags, no IDs)', () => {
    const filter = makeFilter();
    expect(profileFilterMatchesOrchestration(filter, 'orch-1', ['tag-a'])).toBe(false);
    expect(profileFilterMatchesOrchestration(filter, 'orch-1', [])).toBe(false);
    expect(profileFilterMatchesOrchestration(filter, 'orch-1', undefined)).toBe(false);
  });

  it('matches with wildcard tag', () => {
    const filter = makeFilter({ tags: ['*'] });
    expect(profileFilterMatchesOrchestration(filter, 'orch-1', ['tag-a'])).toBe(true);
    expect(profileFilterMatchesOrchestration(filter, 'orch-2', [])).toBe(true);
    expect(profileFilterMatchesOrchestration(filter, 'orch-3', undefined)).toBe(true);
  });

  it('excluded IDs take precedence over wildcard', () => {
    const filter = makeFilter({ tags: ['*'], excludeOrchestrationIds: ['orch-excluded'] });
    expect(profileFilterMatchesOrchestration(filter, 'orch-1', ['tag-a'])).toBe(true);
    expect(profileFilterMatchesOrchestration(filter, 'orch-excluded', ['tag-a'])).toBe(false);
  });

  it('matches by tag intersection', () => {
    const filter = makeFilter({ tags: ['production', 'monitoring'] });
    expect(profileFilterMatchesOrchestration(filter, 'orch-1', ['production'])).toBe(true);
    expect(profileFilterMatchesOrchestration(filter, 'orch-2', ['monitoring', 'alerts'])).toBe(true);
    expect(profileFilterMatchesOrchestration(filter, 'orch-3', ['development'])).toBe(false);
    expect(profileFilterMatchesOrchestration(filter, 'orch-4', [])).toBe(false);
  });

  it('tag comparison is case-insensitive', () => {
    const filter = makeFilter({ tags: ['Production'] });
    expect(profileFilterMatchesOrchestration(filter, 'orch-1', ['production'])).toBe(true);
    expect(profileFilterMatchesOrchestration(filter, 'orch-2', ['PRODUCTION'])).toBe(true);
    expect(profileFilterMatchesOrchestration(filter, 'orch-3', ['PrOdUcTiOn'])).toBe(true);
  });

  it('matches by explicit orchestration ID', () => {
    const filter = makeFilter({ orchestrationIds: ['orch-1', 'orch-2'] });
    expect(profileFilterMatchesOrchestration(filter, 'orch-1', [])).toBe(true);
    expect(profileFilterMatchesOrchestration(filter, 'orch-2', ['some-tag'])).toBe(true);
    expect(profileFilterMatchesOrchestration(filter, 'orch-3', [])).toBe(false);
  });

  it('excluded IDs override explicit inclusion', () => {
    const filter = makeFilter({ orchestrationIds: ['orch-1'], excludeOrchestrationIds: ['orch-1'] });
    expect(profileFilterMatchesOrchestration(filter, 'orch-1', [])).toBe(false);
  });

  it('excluded IDs override tag match', () => {
    const filter = makeFilter({ tags: ['production'], excludeOrchestrationIds: ['orch-1'] });
    expect(profileFilterMatchesOrchestration(filter, 'orch-1', ['production'])).toBe(false);
    expect(profileFilterMatchesOrchestration(filter, 'orch-2', ['production'])).toBe(true);
  });

  it('combined tags and IDs use union behavior', () => {
    const filter = makeFilter({ tags: ['monitoring'], orchestrationIds: ['orch-special'] });
    // Matches by tag
    expect(profileFilterMatchesOrchestration(filter, 'orch-1', ['monitoring'])).toBe(true);
    // Matches by explicit ID
    expect(profileFilterMatchesOrchestration(filter, 'orch-special', [])).toBe(true);
    // No match
    expect(profileFilterMatchesOrchestration(filter, 'orch-other', ['production'])).toBe(false);
  });

  it('handles undefined tags gracefully', () => {
    const filter = makeFilter({ tags: ['production'] });
    expect(profileFilterMatchesOrchestration(filter, 'orch-1', undefined)).toBe(false);
  });
});

// ── getMatchingProfiles ──────────────────────────────────────────────────────

describe('getMatchingProfiles', () => {
  const makeProfile = (id: string, filter: Partial<ProfileFilter>): Profile => ({
    id,
    name: id,
    isActive: true,
    filter: { tags: [], orchestrationIds: [], excludeOrchestrationIds: [], ...filter },
    createdAt: '',
    updatedAt: '',
  });

  it('returns profiles matching by tag', () => {
    const profiles = [
      makeProfile('p1', { tags: ['production'] }),
      makeProfile('p2', { tags: ['staging'] }),
      makeProfile('p3', { tags: ['*'] }),
    ];
    const result = getMatchingProfiles(profiles, 'orch-1', ['production']);
    expect(result.map(p => p.id)).toEqual(['p1', 'p3']);
  });

  it('returns empty array when no profile matches', () => {
    const profiles = [
      makeProfile('p1', { tags: ['staging'] }),
    ];
    expect(getMatchingProfiles(profiles, 'orch-1', ['production'])).toEqual([]);
  });

  it('case-insensitive tag matching across profiles', () => {
    const profiles = [
      makeProfile('p1', { tags: ['PRODUCTION'] }),
    ];
    const result = getMatchingProfiles(profiles, 'orch-1', ['production']);
    expect(result.map(p => p.id)).toEqual(['p1']);
  });
});

// ── orchestrationMatchesProfileFilter ────────────────────────────────────────

describe('orchestrationMatchesProfileFilter', () => {
  const makeProfile = (id: string, filter: Partial<ProfileFilter>): Profile => ({
    id,
    name: id,
    isActive: true,
    filter: { tags: [], orchestrationIds: [], excludeOrchestrationIds: [], ...filter },
    createdAt: '',
    updatedAt: '',
  });

  it('returns true when no profiles are selected (show all)', () => {
    expect(orchestrationMatchesProfileFilter('orch-1', ['tag-a'], [], [])).toBe(true);
  });

  it('returns true when orchestration matches a selected profile', () => {
    const profiles = [makeProfile('p1', { tags: ['production'] })];
    expect(orchestrationMatchesProfileFilter('orch-1', ['production'], ['p1'], profiles)).toBe(true);
  });

  it('returns false when orchestration does not match any selected profile', () => {
    const profiles = [makeProfile('p1', { tags: ['staging'] })];
    expect(orchestrationMatchesProfileFilter('orch-1', ['production'], ['p1'], profiles)).toBe(false);
  });

  it('case-insensitive tag matching through profile filter', () => {
    const profiles = [makeProfile('p1', { tags: ['Production'] })];
    expect(orchestrationMatchesProfileFilter('orch-1', ['production'], ['p1'], profiles)).toBe(true);
  });

  it('ignores profiles not in the selected list', () => {
    const profiles = [
      makeProfile('p1', { tags: ['production'] }),
      makeProfile('p2', { tags: ['staging'] }),
    ];
    // Only p2 is selected; orch has 'production' tag — should not match
    expect(orchestrationMatchesProfileFilter('orch-1', ['production'], ['p2'], profiles)).toBe(false);
  });
});

// ── orchestrationMatchesSearch ───────────────────────────────────────────────

describe('orchestrationMatchesSearch', () => {
  const orch = {
    name: 'Email Notifier',
    description: 'Sends email notifications for production alerts',
    triggerType: 'Scheduler',
    tags: ['production', 'email', 'monitoring'],
    steps: [{ name: 'fetch-data' }, { name: 'send-email' }],
  };

  it('returns true when query is empty', () => {
    expect(orchestrationMatchesSearch(orch, '')).toBe(true);
  });

  it('matches by name', () => {
    expect(orchestrationMatchesSearch(orch, 'Email')).toBe(true);
    expect(orchestrationMatchesSearch(orch, 'notifier')).toBe(true);
  });

  it('matches by description', () => {
    expect(orchestrationMatchesSearch(orch, 'alerts')).toBe(true);
  });

  it('matches by trigger type', () => {
    expect(orchestrationMatchesSearch(orch, 'scheduler')).toBe(true);
  });

  it('matches by tag', () => {
    expect(orchestrationMatchesSearch(orch, 'monitoring')).toBe(true);
    expect(orchestrationMatchesSearch(orch, 'prod')).toBe(true); // partial match
  });

  it('matches by step name', () => {
    expect(orchestrationMatchesSearch(orch, 'fetch')).toBe(true);
    expect(orchestrationMatchesSearch(orch, 'send-email')).toBe(true);
  });

  it('is case-insensitive', () => {
    expect(orchestrationMatchesSearch(orch, 'EMAIL')).toBe(true);
    expect(orchestrationMatchesSearch(orch, 'PRODUCTION')).toBe(true);
  });

  it('returns false when nothing matches', () => {
    expect(orchestrationMatchesSearch(orch, 'nonexistent')).toBe(false);
  });

  it('handles orchestration with no tags', () => {
    const noTags = { name: 'Test', steps: [] };
    expect(orchestrationMatchesSearch(noTags, 'Test')).toBe(true);
    expect(orchestrationMatchesSearch(noTags, 'tag')).toBe(false);
  });

  it('handles string steps', () => {
    const withStringSteps = { name: 'Test', steps: ['step-one', 'step-two'] };
    expect(orchestrationMatchesSearch(withStringSteps, 'step-one')).toBe(true);
    expect(orchestrationMatchesSearch(withStringSteps, 'step-three')).toBe(false);
  });
});
