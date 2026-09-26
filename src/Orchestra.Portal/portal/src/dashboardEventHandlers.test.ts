import { describe, it, expect, vi } from 'vitest';
import { createDashboardEventHandlers, type DashboardResyncActions } from './dashboardEventHandlers';

/** Build a DashboardResyncActions whose members are all spies. Concrete `() => {}` impls keep
 *  vitest from inferring a constructable mock signature, so the object stays assignable. */
function makeActions() {
  return {
    reloadAll: vi.fn(() => {}),
    reloadProfiles: vi.fn(() => {}),
    refreshOrchestrations: vi.fn(() => {}),
    refreshActive: vi.fn(() => {}),
    refreshHistory: vi.fn(() => {}),
    refreshPendingInputs: vi.fn(() => {}),
    applyAwaitingInput: vi.fn((_evt: unknown) => {}),
    applyInputReceived: vi.fn((_evt: unknown) => {}),
    applyInputTimeout: vi.fn((_evt: unknown) => {}),
  } satisfies DashboardResyncActions;
}

describe('createDashboardEventHandlers', () => {
  it('onConnected does a full snapshot re-sync including Active + Recent (stale-until-refresh regression)', () => {
    // Regression guard: the backend keeps no event replay, so on every (re)connect the client
    // must re-fetch server-owned snapshots. Previously onConnected re-synced profiles/orchestrations
    // /pending-inputs but NOT the Active or Recent Executions lists, so those stayed stale until a
    // manual browser refresh. reloadAll() re-fetches orchestrations + history + active.
    const actions = makeActions();
    createDashboardEventHandlers(actions).onConnected!();

    expect(actions.reloadAll).toHaveBeenCalledTimes(1);
    expect(actions.reloadProfiles).toHaveBeenCalledTimes(1);
    expect(actions.refreshPendingInputs).toHaveBeenCalledTimes(1);
  });

  it('onExecutionStarted refreshes BOTH the Active list and Recent Executions', () => {
    // Recent Executions renders running rows too, so a newly started run must appear there
    // immediately, not only when it completes.
    const actions = makeActions();
    createDashboardEventHandlers(actions).onExecutionStarted!({
      executionId: 'e1', orchestrationId: 'o1', orchestrationName: 'demo', triggeredBy: 'scheduler',
    });

    expect(actions.refreshActive).toHaveBeenCalledTimes(1);
    expect(actions.refreshHistory).toHaveBeenCalledTimes(1);
  });

  it('onExecutionCompleted refreshes the Active list and Recent Executions', () => {
    const actions = makeActions();
    createDashboardEventHandlers(actions).onExecutionCompleted!({
      executionId: 'e1', orchestrationId: 'o1', orchestrationName: 'demo', status: 'Succeeded',
    });

    expect(actions.refreshActive).toHaveBeenCalledTimes(1);
    expect(actions.refreshHistory).toHaveBeenCalledTimes(1);
  });

  it('profile events refresh profiles/orchestrations without touching the run lists', () => {
    const actions = makeActions();
    const handlers = createDashboardEventHandlers(actions);

    handlers.onProfileActiveSetChanged!({ activatedOrchestrationIds: [], deactivatedOrchestrationIds: [], trigger: 'manual' });
    handlers.onProfilesChanged!({ reason: 'file-sync' });

    expect(actions.reloadProfiles).toHaveBeenCalledTimes(2);
    expect(actions.refreshOrchestrations).toHaveBeenCalledTimes(1);
    // Run-list snapshots must not be pulled for profile-only changes.
    expect(actions.reloadAll).not.toHaveBeenCalled();
    expect(actions.refreshActive).not.toHaveBeenCalled();
    expect(actions.refreshHistory).not.toHaveBeenCalled();
  });

  it('input lifecycle events delegate to the pending-input store', () => {
    const actions = makeActions();
    const handlers = createDashboardEventHandlers(actions);
    const record = { orchestrationName: 'demo', runId: 'r1', stepName: 'ask' } as never;

    handlers.onAwaitingInput!(record);
    handlers.onInputReceived!(record);
    handlers.onInputTimeout!(record);

    expect(actions.applyAwaitingInput).toHaveBeenCalledWith(record);
    expect(actions.applyInputReceived).toHaveBeenCalledWith(record);
    expect(actions.applyInputTimeout).toHaveBeenCalledWith(record);
  });
});
