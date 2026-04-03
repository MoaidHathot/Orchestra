import { describe, it, expect } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useZoomPan } from './useZoomPan';

// ── Constants (mirrored from hook source) ────────────────────────────────────

const MIN_SCALE = 0.25;
const MAX_SCALE = 3;
const ZOOM_STEP = 0.15;

// ── Helpers ──────────────────────────────────────────────────────────────────

/** Round to avoid floating-point drift in assertions. */
const round = (n: number) => Math.round(n * 1000) / 1000;

// ── Zoom behaviour ───────────────────────────────────────────────────────────

describe('useZoomPan – zoom', () => {
  it('starts at scale 1', () => {
    const { result } = renderHook(() => useZoomPan());
    expect(result.current.scale).toBe(1);
  });

  it('zoomIn increases scale by ZOOM_STEP', () => {
    const { result } = renderHook(() => useZoomPan());
    act(() => result.current.zoomIn());
    expect(round(result.current.scale)).toBe(round(1 + ZOOM_STEP));
  });

  it('zoomOut decreases scale by ZOOM_STEP', () => {
    const { result } = renderHook(() => useZoomPan());
    act(() => result.current.zoomOut());
    expect(round(result.current.scale)).toBe(round(1 - ZOOM_STEP));
  });

  it('resetZoom returns to 1', () => {
    const { result } = renderHook(() => useZoomPan());
    act(() => {
      result.current.zoomIn();
      result.current.zoomIn();
    });
    expect(result.current.scale).not.toBe(1);

    act(() => result.current.resetZoom());
    expect(result.current.scale).toBe(1);
  });

  it('scale never exceeds MAX_SCALE', () => {
    const { result } = renderHook(() => useZoomPan());

    // Zoom in many times to exceed the limit
    act(() => {
      for (let i = 0; i < 50; i++) {
        result.current.zoomIn();
      }
    });

    expect(result.current.scale).toBe(MAX_SCALE);
  });

  it('scale never goes below MIN_SCALE', () => {
    const { result } = renderHook(() => useZoomPan());

    // Zoom out many times to go below the limit
    act(() => {
      for (let i = 0; i < 50; i++) {
        result.current.zoomOut();
      }
    });

    expect(result.current.scale).toBe(MIN_SCALE);
  });

  it('zoomIn then zoomOut round-trips to the original scale', () => {
    const { result } = renderHook(() => useZoomPan());

    act(() => {
      result.current.zoomIn();
      result.current.zoomOut();
    });

    expect(round(result.current.scale)).toBe(1);
  });

  it('multiple zoomIn calls accumulate', () => {
    const { result } = renderHook(() => useZoomPan());

    act(() => {
      result.current.zoomIn();
      result.current.zoomIn();
      result.current.zoomIn();
    });

    expect(round(result.current.scale)).toBe(round(1 + ZOOM_STEP * 3));
  });
});

// ── Panning state ────────────────────────────────────────────────────────────

describe('useZoomPan – panning state', () => {
  it('isPanning defaults to false', () => {
    const { result } = renderHook(() => useZoomPan());
    expect(result.current.isPanning).toBe(false);
  });
});

// ── Refs ──────────────────────────────────────────────────────────────────────

describe('useZoomPan – refs', () => {
  it('provides a containerRef', () => {
    const { result } = renderHook(() => useZoomPan());
    expect(result.current.containerRef).toBeDefined();
    expect(result.current.containerRef.current).toBeNull();
  });

  it('provides a viewportRef', () => {
    const { result } = renderHook(() => useZoomPan());
    expect(result.current.viewportRef).toBeDefined();
    expect(result.current.viewportRef.current).toBeNull();
  });
});

// ── Return shape ─────────────────────────────────────────────────────────────

describe('useZoomPan – return shape', () => {
  it('returns all expected members', () => {
    const { result } = renderHook(() => useZoomPan());
    const state = result.current;

    expect(typeof state.scale).toBe('number');
    expect(typeof state.zoomIn).toBe('function');
    expect(typeof state.zoomOut).toBe('function');
    expect(typeof state.resetZoom).toBe('function');
    expect(typeof state.isPanning).toBe('boolean');
    expect(state.containerRef).toBeDefined();
    expect(state.viewportRef).toBeDefined();
  });
});
