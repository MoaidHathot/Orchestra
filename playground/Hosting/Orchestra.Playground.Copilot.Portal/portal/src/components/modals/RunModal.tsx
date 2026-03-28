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
      for (const name of orchestration.parameters) {
        initial[name] = '';
      }
      setParams(initial);
    }
  }, [orchestration]);

  if (!orchestration) return null;

  const parameterNames = orchestration.parameters ?? [];

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
          {parameterNames.map((name, i) => (
            <div className="form-group" key={i}>
              <label className="form-label">{name}</label>
              <input
                type="text"
                className="form-input"
                placeholder={`Enter ${name}`}
                value={params[name] || ''}
                onChange={(e: React.ChangeEvent<HTMLInputElement>) =>
                  setParams((prev) => ({ ...prev, [name]: e.target.value }))
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
