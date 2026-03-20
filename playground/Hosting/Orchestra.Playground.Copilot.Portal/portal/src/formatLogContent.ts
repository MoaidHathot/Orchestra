export interface LogEvent {
  type: string;
  actualModel?: string;
  error?: string;
  toolName?: string;
  success?: boolean;
  inputTokens?: number;
  outputTokens?: number;
  chunk?: string;
  displayName?: string;
  agentName?: string;
  tools?: string[];
  [key: string]: unknown;
}

export function formatLogContent(log: LogEvent): string {
  if (log.type === 'step-started') return 'Step started';
  if (log.type === 'step-completed') return `Completed (${log.actualModel || 'unknown model'})`;
  if (log.type === 'step-error') return log.error || '';
  if (log.type === 'tool-started') return `Tool: ${log.toolName}`;
  if (log.type === 'tool-completed') return `Tool ${log.toolName}: ${log.success ? 'success' : 'failed'}`;
  if (log.type === 'usage') return `Tokens: ${log.inputTokens || 0} in / ${log.outputTokens || 0} out`;
  if (log.type === 'content-delta') return (log.chunk?.substring(0, 100) ?? '') + ((log.chunk?.length ?? 0) > 100 ? '...' : '');
  if (log.type === 'subagent-selected') {
    const name = log.displayName || log.agentName || 'unknown';
    const tools = log.tools ? `(tools: ${log.tools.join(', ')})` : '(all tools)';
    return `Selected: ${name} ${tools}`;
  }
  if (log.type === 'subagent-started') {
    const name = log.displayName || log.agentName || 'unknown';
    return `Subagent started: ${name}`;
  }
  if (log.type === 'subagent-completed') {
    const name = log.displayName || log.agentName || 'unknown';
    return `Subagent completed: ${name}`;
  }
  if (log.type === 'subagent-failed') {
    const name = log.displayName || log.agentName || 'unknown';
    return `Subagent failed: ${name} - ${log.error || 'unknown error'}`;
  }
  if (log.type === 'subagent-deselected') return 'Returned to parent agent';
  return JSON.stringify(log).substring(0, 200);
}
