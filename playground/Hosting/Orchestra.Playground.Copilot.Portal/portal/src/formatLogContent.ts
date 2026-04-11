export interface LogEvent {
  type: string;
  actualModel?: string;
  stepType?: string;
  error?: string;
  toolName?: string;
  success?: boolean;
  inputTokens?: number;
  outputTokens?: number;
  chunk?: string;
  displayName?: string;
  agentName?: string;
  tools?: string[];
  warningType?: string;
  infoType?: string;
  message?: string;
  [key: string]: unknown;
}

export function formatLogContent(log: LogEvent): string {
  if (log.type === 'step-started') return 'Step started';
  if (log.type === 'step-completed') {
    if (log.actualModel) return `Completed (${log.actualModel})`;
    if (log.stepType) return `Completed (${log.stepType})`;
    return 'Completed';
  }
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
  if (log.type === 'session-warning') return `Warning [${log.warningType || 'unknown'}]: ${log.message || ''}`;
  if (log.type === 'session-info') return `Info [${log.infoType || 'unknown'}]: ${log.message || ''}`;
  if (log.type === 'mcp-servers-loaded') {
    const servers = (log.servers as Array<{ name: string; status: string; error?: string }>) || [];
    return `MCP servers loaded: ${servers.map(s => `${s.name}=${s.status}${s.error ? ` (${s.error})` : ''}`).join(', ')}`;
  }
  if (log.type === 'mcp-server-status-changed') {
    return `MCP '${log.serverName || 'unknown'}' -> ${log.status || 'unknown'}`;
  }
  return JSON.stringify(log).substring(0, 200);
}
