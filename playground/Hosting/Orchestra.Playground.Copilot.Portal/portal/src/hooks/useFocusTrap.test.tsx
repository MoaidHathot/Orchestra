import { describe, it, expect, vi } from 'vitest';
import { render, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { useFocusTrap } from './useFocusTrap';

/**
 * Minimal harness that wires the hook to a mounted container, mirroring how the modals
 * attach the returned ref to their overlay.
 */
function Harness({ active, onEscape }: { active: boolean; onEscape?: () => void }) {
  const ref = useFocusTrap<HTMLDivElement>(active, onEscape);
  return (
    <div ref={ref}>
      <button type="button">focusable</button>
    </div>
  );
}

describe('useFocusTrap — Escape policy', () => {
  it('calls onEscape when Escape is pressed and a handler is supplied (read-only viewer modals keep this)', () => {
    const onEscape = vi.fn();
    render(<Harness active={true} onEscape={onEscape} />);

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(onEscape).toHaveBeenCalledTimes(1);
  });

  it('does nothing on Escape when NO handler is supplied (content-entry modals opt out to avoid losing input)', () => {
    // RunModal/AddModal/BuilderModal/etc. call useFocusTrap(open) with no onEscape, so
    // Escape must not trigger any close (and must not throw).
    render(<Harness active={true} />);

    expect(() => fireEvent.keyDown(document, { key: 'Escape' })).not.toThrow();
  });

  it('does not call onEscape while inactive', () => {
    const onEscape = vi.fn();
    render(<Harness active={false} onEscape={onEscape} />);

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(onEscape).not.toHaveBeenCalled();
  });
});
