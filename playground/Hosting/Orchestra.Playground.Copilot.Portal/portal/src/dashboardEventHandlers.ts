import type { DashboardEventHandlers } from './hooks/useDashboardEvents';
import type { PendingInputRecord } from './types';

/**
 * Snapshot-refresh actions the dashboard-events SSE handlers delegate to. Kept as a narrow,
 * injectable interface so the wiring can be unit-tested without rendering the whole App tree.
 */
export interface DashboardResyncActions {
  /** Full snapshot re-sync: orchestrations + Recent Executions (history) + Active lists.
   *  This is the programmatic equivalent of the Portal "Refresh" button. */
  reloadAll: () => void;
  /** Re-fetch the profile selector (its IsActive flags). */
  reloadProfiles: () => void;
  /** Re-fetch the enabled/disabled orchestration cards. */
  refreshOrchestrations: () => void;
  /** Re-fetch just the Active/running list (`/api/active`). */
  refreshActive: () => void;
  /** Re-fetch just the Recent Executions list (`/api/history`). */
  refreshHistory: () => void;
  /** Re-fetch the waiting-input list. */
  refreshPendingInputs: () => void;
  applyAwaitingInput: (evt: PendingInputRecord) => void;
  applyInputReceived: (evt: { orchestrationName: string; runId: string; stepName: string }) => void;
  applyInputTimeout: (evt: { orchestrationName: string; runId: string; stepName: string }) => void;
}

/**
 * Builds the {@link DashboardEventHandlers} the Portal hands to `useDashboardEvents`.
 *
 * The backend {@code DashboardEventBroadcaster} retains **no event history** (every event uses
 * sequence 0; its contract is "late joiners do a full refresh on connect"). So whenever the
 * `/api/events` stream drops — host restart, sleep/wake, network blip, proxy idle-timeout, or
 * even the initial mount race — any `execution-started`/`execution-completed` emitted during the
 * gap is lost from the live channel. The client must therefore re-fetch the server-owned
 * snapshots on every (re)connect.
 *
 * The previous wiring re-synced profiles/orchestrations/pending-inputs on connect but NOT the
 * Active or Recent Executions lists, so those two stayed stale until `loadData()` ran on mount,
 * the Refresh button, or a browser refresh — exactly the "a refresh fixes it" symptom. Here
 * `onConnected` does a full re-sync (via {@link DashboardResyncActions.reloadAll}), and
 * `onExecutionStarted` refreshes history too so a new running row shows in Recent immediately
 * (Recent Executions includes running runs) instead of only on completion.
 */
export function createDashboardEventHandlers(actions: DashboardResyncActions): DashboardEventHandlers {
  return {
    onConnected: () => {
      // (Re)connected: re-fetch every server-owned snapshot so the dashboard matches true
      // backend state. reloadAll covers orchestrations + Recent Executions + Active — the two
      // lists that previously went stale until a manual refresh.
      actions.reloadAll();
      actions.reloadProfiles();
      actions.refreshPendingInputs();
    },
    onProfileActiveSetChanged: () => {
      // A profile's active state flipped (scheduled transition, manual toggle from another tab).
      actions.reloadProfiles();
      actions.refreshOrchestrations();
    },
    onProfilesChanged: () => {
      // The profile list itself changed (file added/updated/deleted in the watched directory).
      actions.reloadProfiles();
    },
    onExecutionStarted: () => {
      // A new execution started (trigger, manual, resume). Surface it in BOTH the Active list
      // and Recent Executions (which renders running rows) immediately, rather than waiting for
      // completion or the next conditional poll.
      actions.refreshActive();
      actions.refreshHistory();
    },
    onExecutionCompleted: () => {
      // An execution finished — move it from Active to Recent Executions.
      actions.refreshActive();
      actions.refreshHistory();
    },
    onAwaitingInput: (evt) => {
      actions.applyAwaitingInput(evt);
    },
    onInputReceived: (evt) => {
      actions.applyInputReceived(evt);
    },
    onInputTimeout: (evt) => {
      actions.applyInputTimeout(evt);
    },
  };
}
