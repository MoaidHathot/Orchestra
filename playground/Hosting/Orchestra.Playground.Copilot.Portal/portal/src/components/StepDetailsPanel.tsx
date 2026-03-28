import React from 'react';
import type { Step } from '../types';
import CollapsibleText from './CollapsibleText';

interface Props {
  step: Step | string | null;
}

/**
 * Extended step properties that may exist on step objects from the server
 * but are not part of the base Step type definition.
 */
interface ExtendedStep extends Step {
  inputHandlerPrompt?: string;
  outputHandlerPrompt?: string;
  systemPromptMode?: string;
  mcp?: unknown;
  [key: string]: unknown;
}

/** Recursive JSON-like value for rendering arbitrary nested data. */
// eslint-disable-next-line @typescript-eslint/no-redundant-type-constituents
type JsonPrimitive = string | number | boolean | null | undefined;
interface JsonObject {
  [key: string]: JsonValue;
}
type JsonValue = JsonPrimitive | JsonValue[] | JsonObject;

/** Step-level MCP entry shape (may be string or object with name/type). */
type McpEntry = string | { name?: string; type?: string };

function renderValue(value: JsonValue, depth = 0): React.ReactNode {
  if (value === null || value === undefined) {
    return <span className="text-muted">null</span>;
  }
  if (typeof value === 'boolean') {
    return <span style={{ color: '#79c0ff' }}>{value.toString()}</span>;
  }
  if (typeof value === 'number') {
    return <span style={{ color: '#a5d6ff' }}>{value}</span>;
  }
  if (typeof value === 'string') {
    return (
      <span style={{ color: '#a5d6a7', wordBreak: 'break-word', whiteSpace: 'pre-wrap' }}>
        &quot;{value}&quot;
      </span>
    );
  }
  if (Array.isArray(value)) {
    if (value.length === 0) {
      return <span className="text-muted">[]</span>;
    }
    return (
      <div style={{ paddingLeft: depth > 0 ? '12px' : 0 }}>
        {(value as JsonValue[]).map((item: JsonValue, i: number) => (
          <div key={i} style={{ marginBottom: '4px' }}>
            <span className="text-muted">[{i}]</span> {renderValue(item, depth + 1)}
          </div>
        ))}
      </div>
    );
  }
  if (typeof value === 'object') {
    return (
      <div style={{ paddingLeft: depth > 0 ? '12px' : 0 }}>
        {Object.entries(value as JsonObject).map(([k, v]) => (
          <div key={k} style={{ marginBottom: '4px' }}>
            <span style={{ color: '#ff7b72' }}>{k}:</span> {renderValue(v, depth + 1)}
          </div>
        ))}
      </div>
    );
  }
  return String(value);
}

const KNOWN_PROPS: ReadonlySet<string> = new Set([
  'name',
  'prompt',
  'userPrompt',
  'model',
  'dependsOn',
  'condition',
  'output',
  'tools',
  'mcp',
  'mcps',
  'temperature',
  'maxTokens',
  'systemPrompt',
  'reasoningLevel',
  'loopConfig',
  'parameters',
  'inputHandlerPrompt',
  'outputHandlerPrompt',
  'type',
  'systemPromptMode',
]);

export default function StepDetailsPanel({ step }: Props): React.JSX.Element | null {
  if (!step) return null;

  const isSimpleStep = typeof step === 'string';
  const stepName = isSimpleStep ? step : step.name;

  if (isSimpleStep) {
    return (
      <div>
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Name
          </div>
          <div style={{ fontWeight: 600 }}>{stepName}</div>
        </div>
        <div className="text-muted" style={{ fontSize: '12px' }}>
          Step details not available (simple step reference)
        </div>
      </div>
    );
  }

  // Treat as extended step for accessing additional server-provided fields
  const ext = step as ExtendedStep;

  // Collect unknown properties
  const otherProps = Object.keys(ext).filter(
    (k) => !KNOWN_PROPS.has(k) && ext[k] !== undefined && ext[k] !== null,
  );

  return (
    <div style={{ fontSize: '13px' }}>
      {/* Name */}
      <div style={{ marginBottom: '12px' }}>
        <div
          className="text-muted"
          style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
        >
          Name
        </div>
        <div style={{ fontWeight: 600 }}>{step.name}</div>
      </div>

      {/* Type */}
      {step.type != null && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Type
          </div>
          <div style={{ color: '#d2a8ff' }}>{step.type}</div>
        </div>
      )}

      {/* Model */}
      {step.model != null && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Model
          </div>
          <div style={{ color: '#58a6ff' }}>{step.model}</div>
        </div>
      )}

      {/* Parameters */}
      {step.parameters != null && step.parameters.length > 0 && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Required Parameters
          </div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px' }}>
            {step.parameters.map((paramName, i) => (
              <span
                key={i}
                style={{
                  background: '#3fb9504d',
                  border: '1px solid #3fb950',
                  padding: '2px 8px',
                  borderRadius: '4px',
                  fontSize: '12px',
                  color: '#7ee787',
                }}
              >
                {paramName}
              </span>
            ))}
          </div>
        </div>
      )}

      {/* Dependencies */}
      {step.dependsOn != null && step.dependsOn.length > 0 && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Depends On
          </div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px' }}>
            {step.dependsOn.map((dep, i) => (
              <span
                key={i}
                style={{
                  background: 'var(--bg)',
                  padding: '2px 8px',
                  borderRadius: '4px',
                  fontSize: '12px',
                }}
              >
                {dep}
              </span>
            ))}
          </div>
        </div>
      )}

      {/* Input Handler */}
      {ext.inputHandlerPrompt != null && (
        <CollapsibleText
          label="Input Handler"
          text={ext.inputHandlerPrompt}
          maxCollapsedHeight={50}
        />
      )}

      {/* Output Handler */}
      {ext.outputHandlerPrompt != null && (
        <CollapsibleText
          label="Output Handler"
          text={ext.outputHandlerPrompt}
          maxCollapsedHeight={50}
        />
      )}

      {/* User Prompt / Prompt */}
      {(step.userPrompt || step.prompt) != null && (
        <CollapsibleText label="User Prompt" text={step.userPrompt || step.prompt || null} />
      )}

      {/* System Prompt */}
      {step.systemPrompt != null && (
        <CollapsibleText label="System Prompt" text={step.systemPrompt} />
      )}

      {/* System Prompt Mode */}
      {ext.systemPromptMode != null && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            System Prompt Mode
          </div>
          <div style={{ color: '#ffa657' }}>{ext.systemPromptMode}</div>
        </div>
      )}

      {/* Condition */}
      {step.condition != null && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Condition
          </div>
          <div style={{ color: '#ffa657' }}>{step.condition}</div>
        </div>
      )}

      {/* Output */}
      {step.output != null && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Output
          </div>
          <div>{renderValue(step.output as JsonValue)}</div>
        </div>
      )}

      {/* Tools */}
      {step.tools != null && step.tools.length > 0 && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Tools ({step.tools.length})
          </div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px' }}>
            {step.tools.map((tool, i) => (
              <span
                key={i}
                style={{
                  background: 'var(--bg)',
                  padding: '2px 8px',
                  borderRadius: '4px',
                  fontSize: '12px',
                  color: '#7ee787',
                }}
              >
                {typeof tool === 'string' ? tool : JSON.stringify(tool)}
              </span>
            ))}
          </div>
        </div>
      )}

      {/* MCPs (step-level) */}
      {step.mcps != null && step.mcps.length > 0 && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            MCPs ({step.mcps.length})
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '4px' }}>
            {(step.mcps as unknown as McpEntry[]).map((mcp, i) => (
              <div
                key={i}
                style={{
                  background: 'var(--bg)',
                  padding: '6px 8px',
                  borderRadius: '4px',
                  fontSize: '12px',
                }}
              >
                <span style={{ color: '#a371f7' }}>
                  {typeof mcp === 'string' ? mcp : mcp.name || String(mcp)}
                </span>
                {typeof mcp !== 'string' && mcp.type && (
                  <span className="text-muted" style={{ marginLeft: '8px' }}>
                    ({mcp.type})
                  </span>
                )}
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Legacy MCP field */}
      {ext.mcp != null && !step.mcps && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            MCP
          </div>
          <div>{renderValue(ext.mcp as JsonValue)}</div>
        </div>
      )}

      {/* Temperature */}
      {step.temperature !== undefined && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Temperature
          </div>
          <div>{step.temperature}</div>
        </div>
      )}

      {/* Max Tokens */}
      {step.maxTokens !== undefined && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Max Tokens
          </div>
          <div>{step.maxTokens}</div>
        </div>
      )}

      {/* Reasoning Level */}
      {step.reasoningLevel != null && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Reasoning Level
          </div>
          <div>{step.reasoningLevel}</div>
        </div>
      )}

      {/* Loop Config */}
      {step.loopConfig != null && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Loop Config
          </div>
          <div>{renderValue(step.loopConfig as unknown as JsonValue)}</div>
        </div>
      )}

      {/* Other properties */}
      {otherProps.length > 0 && (
        <div style={{ marginBottom: '12px' }}>
          <div
            className="text-muted"
            style={{ fontSize: '11px', textTransform: 'uppercase', marginBottom: '4px' }}
          >
            Other Properties
          </div>
          {otherProps.map((prop) => (
            <div key={prop} style={{ marginBottom: '8px' }}>
              <span style={{ color: '#ff7b72' }}>{prop}:</span>{' '}
              {renderValue(ext[prop] as JsonValue)}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
