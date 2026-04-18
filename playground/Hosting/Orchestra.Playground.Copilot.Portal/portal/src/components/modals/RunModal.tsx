import React, { useState, useEffect } from 'react';
import type { Orchestration, InputDefinition } from '../../types';
import { Icons } from '../../icons';
import { useFocusTrap } from '../../hooks/useFocusTrap';

interface Props {
  open: boolean;
  orchestration: Orchestration | null;
  onClose: () => void;
  onRun: (params: Record<string, string>) => void;
}

export default function RunModal({ open, orchestration, onClose, onRun }: Props): React.JSX.Element | null {
  const trapRef = useFocusTrap<HTMLDivElement>(open, onClose);
  const [params, setParams] = useState<Record<string, string>>({});

  // Build the list of inputs to render: prefer typed inputs, fall back to legacy parameters
  const inputEntries: { name: string; def: InputDefinition | null }[] = [];
  if (orchestration?.inputs && Object.keys(orchestration.inputs).length > 0) {
    for (const [name, def] of Object.entries(orchestration.inputs)) {
      inputEntries.push({ name, def });
    }
  } else if (orchestration?.parameters) {
    for (const name of orchestration.parameters) {
      inputEntries.push({ name, def: null });
    }
  }

  useEffect(() => {
    const initial: Record<string, string> = {};
    for (const { name, def } of inputEntries) {
      if (def?.type === 'boolean') {
        initial[name] = def?.default ?? '';
      } else {
        initial[name] = '';
      }
    }
    setParams(initial);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [orchestration]);

  if (!orchestration) return null;

  const hasAnyMultiline = inputEntries.some(e => e.def?.multiline);

  const set = (name: string, value: string) =>
    setParams((prev) => ({ ...prev, [name]: value }));

  return (
    <div
      className={`modal-overlay ${open ? 'visible' : ''}`}
      ref={trapRef}
      onClick={(e: React.MouseEvent<HTMLDivElement>) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div
        className={hasAnyMultiline ? 'modal modal-lg' : 'modal modal-sm'}
        role="dialog"
        aria-modal="true"
        aria-label="Run orchestration"
      >
        <div className="modal-header">
          <div className="modal-title">Run {orchestration.name}</div>
          <button className="modal-close" aria-label="Close" onClick={onClose}>
            <Icons.X />
          </button>
        </div>
        <div className="modal-body">
          {inputEntries.length > 0 && (
            <p className="text-muted" style={{ marginBottom: '16px' }}>
              Enter parameters for this orchestration:
            </p>
          )}
          {inputEntries.map(({ name, def }) => (
            <InputField
              key={name}
              name={name}
              def={def}
              value={params[name] ?? ''}
              onChange={(v) => set(name, v)}
            />
          ))}
          {inputEntries.length === 0 && (
            <p className="text-muted">
              This orchestration has no parameters.
            </p>
          )}
        </div>
        <div className="modal-footer">
          <button className="btn" onClick={onClose}>
            Cancel
          </button>
          <button className="btn btn-success" onClick={() => {
            // Filter out empty values so optional inputs with defaults work correctly
            const nonEmpty: Record<string, string> = {};
            for (const [k, v] of Object.entries(params)) {
              if (v.length > 0) nonEmpty[k] = v;
            }
            onRun(nonEmpty);
          }}>
            <Icons.Play /> Run
          </button>
        </div>
      </div>
    </div>
  );
}

/* ── Per-input field renderer ────────────────────────────────────────── */

function InputField({
  name,
  def,
  value,
  onChange,
}: {
  name: string;
  def: InputDefinition | null;
  value: string;
  onChange: (v: string) => void;
}) {
  const type = def?.type ?? 'string';
  const isRequired = def?.required !== false;
  const placeholder = def?.default
    ? `Default: ${def.default}`
    : `Enter ${name}`;

  return (
    <div className="form-group">
      <label className="form-label" style={{ display: 'flex', alignItems: 'center', gap: '6px' }}>
        {name}
        {def && !isRequired && (
          <span style={{
            fontSize: '9px',
            padding: '1px 5px',
            borderRadius: '3px',
            background: 'rgba(139, 148, 158, 0.15)',
            color: 'var(--text-dim)',
            fontWeight: 400,
          }}>
            optional
          </span>
        )}
        {def?.type && def.type !== 'string' && (
          <span style={{
            fontSize: '9px',
            padding: '1px 5px',
            borderRadius: '3px',
            background: 'rgba(88, 166, 255, 0.12)',
            color: '#58a6ff',
            fontWeight: 400,
          }}>
            {def.type}
          </span>
        )}
      </label>

      {def?.description && (
        <div style={{
          fontSize: '11px',
          color: 'var(--text-dim)',
          marginBottom: '4px',
          lineHeight: '1.4',
        }}>
          {def.description}
        </div>
      )}

      {/* Boolean → toggle */}
      {type === 'boolean' ? (
        <BooleanToggle value={value} onChange={onChange} />
      ) : def?.enum && def.enum.length > 0 ? (
        /* Enum → select dropdown */
        <select
          className="form-input"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          style={{ cursor: 'pointer' }}
        >
          {!isRequired && <option value="">— Default —</option>}
          {isRequired && !value && <option value="">Select...</option>}
          {def.enum.map((opt) => (
            <option key={opt} value={opt}>{opt}</option>
          ))}
        </select>
      ) : def?.multiline ? (
        /* Multiline string → textarea */
        <textarea
          className="form-input"
          placeholder={placeholder}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          rows={6}
          style={{
            resize: 'vertical',
            minHeight: '80px',
            fontFamily: "'SF Mono', Monaco, Consolas, monospace",
            fontSize: '12px',
            lineHeight: '1.5',
          }}
        />
      ) : type === 'number' ? (
        /* Number → number input */
        <input
          type="number"
          className="form-input"
          placeholder={placeholder}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
      ) : (
        /* Default string → text input */
        <input
          type="text"
          className="form-input"
          placeholder={placeholder}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
      )}
    </div>
  );
}

/* ── Boolean toggle switch ────────────────────────────────────────── */

function BooleanToggle({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const isTrue = value.toLowerCase() === 'true';
  const isSet = value === 'true' || value === 'false';

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
      <button
        type="button"
        onClick={() => onChange(isTrue ? 'false' : 'true')}
        style={{
          width: '40px',
          height: '22px',
          borderRadius: '11px',
          border: 'none',
          cursor: 'pointer',
          position: 'relative',
          transition: 'background 0.2s',
          background: isTrue
            ? 'var(--success, #3fb950)'
            : isSet
              ? 'var(--text-dim, #484f58)'
              : 'var(--border, #30363d)',
          flexShrink: 0,
        }}
        aria-label={`Toggle ${isTrue ? 'off' : 'on'}`}
      >
        <span
          style={{
            position: 'absolute',
            top: '2px',
            left: isTrue ? '20px' : '2px',
            width: '18px',
            height: '18px',
            borderRadius: '50%',
            background: '#fff',
            transition: 'left 0.2s',
          }}
        />
      </button>
      <span style={{
        fontSize: '12px',
        color: isSet ? 'var(--text)' : 'var(--text-dim)',
        minWidth: '36px',
      }}>
        {isSet ? (isTrue ? 'true' : 'false') : '—'}
      </span>
      {isSet && (
        <button
          type="button"
          onClick={() => onChange('')}
          style={{
            fontSize: '10px',
            color: 'var(--text-dim)',
            background: 'none',
            border: 'none',
            cursor: 'pointer',
            padding: '2px 4px',
            textDecoration: 'underline',
          }}
        >
          clear
        </button>
      )}
    </div>
  );
}
