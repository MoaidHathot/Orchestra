import React, { useState } from 'react';

interface Props {
  label: string;
  text: string | null | undefined;
  defaultExpanded?: boolean;
  maxCollapsedHeight?: number;
}

export default function CollapsibleText({
  label,
  text,
  defaultExpanded = false,
  maxCollapsedHeight = 60,
}: Props): React.JSX.Element {
  const [expanded, setExpanded] = useState(defaultExpanded);
  const needsCollapse = text != null && text.length > 150;

  return (
    <div style={{ marginBottom: '12px' }}>
      <div
        className="text-muted"
        style={{
          fontSize: '11px',
          textTransform: 'uppercase',
          marginBottom: '4px',
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          cursor: needsCollapse ? 'pointer' : 'default',
        }}
        onClick={() => needsCollapse && setExpanded(!expanded)}
      >
        <span>{label}</span>
        {needsCollapse && (
          <span style={{ color: '#58a6ff', fontSize: '10px' }}>
            {expanded ? '\u25BC Collapse' : '\u25B6 Expand'}
          </span>
        )}
      </div>
      <div
        style={{
          background: 'var(--bg)',
          padding: '8px',
          borderRadius: '4px',
          fontSize: '12px',
          whiteSpace: 'pre-wrap',
          maxHeight: expanded ? '400px' : `${maxCollapsedHeight}px`,
          overflow: expanded ? 'auto' : 'hidden',
          position: 'relative',
          transition: 'max-height 0.2s ease',
        }}
      >
        {text}
        {!expanded && needsCollapse && (
          <div
            style={{
              position: 'absolute',
              bottom: 0,
              left: 0,
              right: 0,
              height: '30px',
              background: 'linear-gradient(transparent, var(--bg))',
              pointerEvents: 'none',
            }}
          />
        )}
      </div>
    </div>
  );
}
