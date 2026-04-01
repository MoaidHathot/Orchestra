import { describe, it, expect } from 'vitest';
import { isIncompleteExecution } from './utils';

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
