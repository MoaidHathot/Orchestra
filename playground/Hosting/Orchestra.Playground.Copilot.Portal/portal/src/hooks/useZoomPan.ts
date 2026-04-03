import { useState, useCallback, useEffect, useRef } from 'react';

const MIN_SCALE = 0.25;
const MAX_SCALE = 3;
const ZOOM_STEP = 0.15;
const WHEEL_ZOOM_STEP = 0.08;

export interface ZoomPanState {
  scale: number;
  zoomIn: () => void;
  zoomOut: () => void;
  resetZoom: () => void;
  /** Whether the user is currently dragging (panning). */
  isPanning: boolean;
  /** Attach to the scrollable container to capture Ctrl+Wheel events. */
  containerRef: React.RefObject<HTMLDivElement | null>;
  /** Attach to the scrollable viewport to enable mouse-drag panning. */
  viewportRef: React.RefObject<HTMLDivElement | null>;
}

/**
 * Hook that provides zoom-in / zoom-out / reset and mouse-drag panning for a
 * DAG container.
 *
 * Supports:
 *  - Ctrl+Mouse Wheel (or Cmd+Wheel on Mac) for zoom
 *  - Keyboard: Ctrl+= / Ctrl+- / Ctrl+0
 *  - Middle-mouse-button drag or left-mouse-button drag on empty space to pan
 */
export function useZoomPan(): ZoomPanState {
  const [scale, setScale] = useState(1);
  const [isPanning, setIsPanning] = useState(false);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const viewportRef = useRef<HTMLDivElement | null>(null);

  const clamp = (v: number) => Math.min(MAX_SCALE, Math.max(MIN_SCALE, v));

  const zoomIn = useCallback(() => {
    setScale((s) => clamp(s + ZOOM_STEP));
  }, []);

  const zoomOut = useCallback(() => {
    setScale((s) => clamp(s - ZOOM_STEP));
  }, []);

  const resetZoom = useCallback(() => {
    setScale(1);
  }, []);

  // Ctrl+Wheel zoom
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;

    const onWheel = (e: WheelEvent) => {
      if (!e.ctrlKey && !e.metaKey) return;
      e.preventDefault();
      const delta = e.deltaY > 0 ? -WHEEL_ZOOM_STEP : WHEEL_ZOOM_STEP;
      setScale((s) => clamp(s + delta));
    };

    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, []);

  // Keyboard zoom (Ctrl+= / Ctrl+- / Ctrl+0) when container is focused
  // or when the container is hovered.
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;

    let hovered = false;
    const onEnter = () => { hovered = true; };
    const onLeave = () => { hovered = false; };
    el.addEventListener('mouseenter', onEnter);
    el.addEventListener('mouseleave', onLeave);

    const onKeyDown = (e: KeyboardEvent) => {
      if (!hovered && !el.contains(document.activeElement)) return;
      if (!e.ctrlKey && !e.metaKey) return;

      if (e.key === '=' || e.key === '+') {
        e.preventDefault();
        setScale((s) => clamp(s + ZOOM_STEP));
      } else if (e.key === '-') {
        e.preventDefault();
        setScale((s) => clamp(s - ZOOM_STEP));
      } else if (e.key === '0') {
        e.preventDefault();
        setScale(1);
      }
    };

    document.addEventListener('keydown', onKeyDown);
    return () => {
      el.removeEventListener('mouseenter', onEnter);
      el.removeEventListener('mouseleave', onLeave);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, []);

  // Mouse-drag panning on the viewport element
  useEffect(() => {
    const vp = viewportRef.current;
    if (!vp) return;

    let dragging = false;
    let startX = 0;
    let startY = 0;
    let scrollLeft = 0;
    let scrollTop = 0;

    const onMouseDown = (e: MouseEvent) => {
      // Allow panning with middle mouse button, or left button on background
      // (not on interactive elements like buttons/links inside the DAG)
      const isMiddle = e.button === 1;
      const isLeft = e.button === 0;

      if (!isMiddle && !isLeft) return;

      // For left click, only start panning if clicking on the viewport
      // background or SVG (not on buttons, links, or other interactive elements)
      if (isLeft) {
        const target = e.target as HTMLElement;
        const tagName = target.tagName.toLowerCase();
        // Don't hijack clicks on interactive elements
        if (tagName === 'button' || tagName === 'a' || tagName === 'input' ||
            tagName === 'select' || tagName === 'textarea' ||
            target.closest('button') || target.closest('a')) {
          return;
        }
      }

      // Prevent default middle-click auto-scroll behavior
      if (isMiddle) {
        e.preventDefault();
      }

      dragging = true;
      startX = e.clientX;
      startY = e.clientY;
      scrollLeft = vp.scrollLeft;
      scrollTop = vp.scrollTop;
      setIsPanning(true);
      vp.style.cursor = 'grabbing';
      vp.style.userSelect = 'none';
    };

    const onMouseMove = (e: MouseEvent) => {
      if (!dragging) return;
      e.preventDefault();
      const dx = e.clientX - startX;
      const dy = e.clientY - startY;
      vp.scrollLeft = scrollLeft - dx;
      vp.scrollTop = scrollTop - dy;
    };

    const onMouseUp = () => {
      if (!dragging) return;
      dragging = false;
      setIsPanning(false);
      vp.style.cursor = '';
      vp.style.userSelect = '';
    };

    vp.addEventListener('mousedown', onMouseDown);
    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);

    return () => {
      vp.removeEventListener('mousedown', onMouseDown);
      window.removeEventListener('mousemove', onMouseMove);
      window.removeEventListener('mouseup', onMouseUp);
    };
  }, []);

  return { scale, zoomIn, zoomOut, resetZoom, isPanning, containerRef, viewportRef };
}
