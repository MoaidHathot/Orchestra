import { toApiError } from './api';

export interface WorkspaceInfo {
  workspaceDirectory: string; dataPath: string; workflowDirectory: string; showWelcome: boolean;
  hasOrchestrations: boolean; defaultProvider: string; defaultModel: string;
  templates: { id: string; title: string; summary: string; requiresAgent: boolean; requiresPowerShell: boolean }[];
}
export interface SettingsSnapshot {
  configPath: string; source: string; revision: string; error?: string;
  values: Record<string, unknown>; effective: Record<string, unknown>;
  overrides: Record<string, string>; restartRequired: string[];
}
export interface SetupModel { id: string; name: string }
export interface SetupField { key: string; title?: string; description?: string; type: string; hidden?: boolean; required?: boolean; default?: unknown; options?: { value: string; label: string }[] }
export interface SetupMethod { id: string; type: string; label: string; form?: SetupField[] }
export interface SetupIntegration { id: string; name: string; connected: boolean; methods: SetupMethod[] }
export interface ProviderReadiness { provider: string; installed: boolean; ready: boolean; detail: string; models: SetupModel[]; integrations: SetupIntegration[] }
export interface SetupJob {
  id: string; provider: string; action: string; state: string;
  progress: { message: string; authorizationUrl?: string; userCode?: string; requiresCode?: boolean };
  result?: ProviderReadiness;
}
export interface ProviderSetupState { providers: ProviderReadiness[]; jobs: SetupJob[] }
export interface WorkspaceEntry { id: string; name: string; path: string }

/** Setup writes are deliberate operations: do not retry or queue them invisibly when offline. */
export async function workspaceRequest<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const url = `/api/workspace${path}`;
  const response = await fetch(url, { method: body === undefined ? 'GET' : 'POST', signal, cache: 'no-store',
    ...(body !== undefined ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {}) });
  if (!response.ok) throw await toApiError(response, url);
  const text = await response.text();
  return text ? JSON.parse(text) as T : undefined as T;
}
