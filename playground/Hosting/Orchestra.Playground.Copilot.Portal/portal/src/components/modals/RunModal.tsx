import React, { useState, useEffect } from 'react';
import type { Orchestration } from '../../types';
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

  useEffect(() => {
    if (orchestration?.parameters) {
      const initial: Record<string, string> = {};
      for (const key of Object.keys(orchestration.parameters)) {
        initial[key] = '';
      }
      setParams(initial);
    }
  }, [orchestration]);

  if (!orchestration) return null;

  // Build a list of { name, description } from the parameters record
  const parameterList = Object.entries(orchestration.parameters ?? {}).map(
    ([name, def]) => ({ name, description: def?.description ?? '' }),
  );

  return (
    <div
      className={`modal-overlay ${open ? 'visible' : ''}`}
      ref={trapRef}
      onClick={(e: React.MouseEvent<HTMLDivElement>) => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div className="modal modal-sm" role="dialog" aria-modal="true" aria-label="Run orchestration">
        <div className="modal-header">
          <div className="modal-title">Run {orchestration.name}</div>
          <button className="modal-close" aria-label="Close" onClick={onClose}>
            <Icons.X />
          </button>
        </div>
        <div className="modal-body">
          <p className="text-muted" style={{ marginBottom: '16px' }}>
            Enter parameters for this orchestration:
          </p>
          {parameterList.map((param, i) => (
            <div className="form-group" key={i}>
              <label className="form-label">{param.name}</label>
              <input
                type="text"
                className="form-input"
                placeholder={param.description || `Enter ${param.name}`}
                value={params[param.name] || ''}
                onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                  setParams((prev) => ({ ...prev, [param.name]: e.target.value }))
                }
              />
            </div>
          ))}
        </div>
        <div className="modal-footer">
          <button className="btn" onClick={onClose}>
            Cancel
          </button>
          <button className="btn btn-success" onClick={() => onRun(params)}>
            <Icons.Play /> Run
          </button>
        </div>
      </div>
    </div>
  );
}
