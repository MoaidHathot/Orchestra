import React, { useCallback } from 'react';
import { Icons } from '../icons';
import { useZoomPan } from '../hooks/useZoomPan';

interface Props {
  /** Extra className applied to the outer wrapper. */
  className?: string;
  /** Extra inline styles for the outer wrapper (e.g. flex sizing). */
  style?: React.CSSProperties;
  /**
   * Ref that callers use to access the inner div (where Mermaid injects SVG).
   * Accepts `useRef<HTMLDivElement>(null)` or a callback ref.
   */
  dagRef?: React.RefObject<HTMLDivElement | null> | ((el: HTMLDivElement | null) => void);
  children?: React.ReactNode;
}

/**
 * Wraps a Mermaid DAG container with zoom controls and Ctrl+Wheel /
 * Ctrl+Plus/Minus keyboard support.
 *
 * The component renders:
 *  - A toolbar with zoom-in / zoom-out / reset buttons and a percentage
 *  - A scrollable viewport that applies CSS `transform: scale()`
 *  - The inner div (via `dagRef`) where Mermaid injects the SVG
 */
export default function ZoomableDag({ className, style, dagRef, children }: Props): React.JSX.Element {
  const { scale, zoomIn, zoomOut, resetZoom, isPanning, containerRef, viewportRef } = useZoomPan();

  // Merge the external dagRef with nothing else – just assign it via callback.
  const innerRefCallback = useCallback(
    (el: HTMLDivElement | null) => {
      if (typeof dagRef === 'function') {
        dagRef(el);
      } else if (dagRef && 'current' in dagRef) {
        (dagRef as React.MutableRefObject<HTMLDivElement | null>).current = el;
      }
    },
    [dagRef],
  );

  // Merge containerRef (for zoom/pan events) via callback ref
  const outerRefCallback = useCallback(
    (el: HTMLDivElement | null) => {
      (containerRef as React.MutableRefObject<HTMLDivElement | null>).current = el;
    },
    [containerRef],
  );

  // Merge viewportRef (for drag panning) via callback ref
  const viewportRefCallback = useCallback(
    (el: HTMLDivElement | null) => {
      (viewportRef as React.MutableRefObject<HTMLDivElement | null>).current = el;
    },
    [viewportRef],
  );

  return (
    <div className={`zoomable-dag ${className ?? ''}`} style={style} ref={outerRefCallback}>
      {/* Zoom toolbar */}
      <div className="dag-zoom-toolbar">
        <button
          className="dag-zoom-btn"
          onClick={zoomOut}
          title="Zoom out (Ctrl+-)"
          aria-label="Zoom out"
        >
          <Icons.ZoomOut />
        </button>
        <span className="dag-zoom-level">{Math.round(scale * 100)}%</span>
        <button
          className="dag-zoom-btn"
          onClick={zoomIn}
          title="Zoom in (Ctrl+=)"
          aria-label="Zoom in"
        >
          <Icons.ZoomIn />
        </button>
        <button
          className="dag-zoom-btn"
          onClick={resetZoom}
          title="Reset zoom (Ctrl+0)"
          aria-label="Reset zoom"
        >
          <Icons.ZoomReset />
        </button>
      </div>

      {/* Scrollable viewport with drag-to-pan */}
      <div
        className="dag-zoom-viewport"
        ref={viewportRefCallback}
        style={{ cursor: isPanning ? 'grabbing' : 'grab' }}
      >
        <div
          className="dag-zoom-content"
          style={{ transform: `scale(${scale})`, transformOrigin: 'top left' }}
        >
          <div ref={innerRefCallback}>
            {children}
          </div>
        </div>
      </div>
    </div>
  );
}
