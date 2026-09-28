import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import '@testing-library/jest-dom';
import CopyButton, { copyToClipboard } from './CopyButton';

vi.mock('../icons', () => ({
  Icons: {
    Copy: () => <span data-testid="icon-copy" />,
    Check: () => <span data-testid="icon-check" />,
  },
}));

describe('CopyButton', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('copies the value, confirms briefly, then reverts', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });

    render(<CopyButton value="abc123" label="execution id" feedbackMs={500} />);
    const button = screen.getByRole('button', { name: 'Copy execution id' });
    expect(screen.getByTestId('icon-copy')).toBeInTheDocument();

    await act(async () => {
      fireEvent.click(button);
      await Promise.resolve();
    });

    expect(writeText).toHaveBeenCalledWith('abc123');
    expect(screen.getByRole('button', { name: 'Copied' })).toBeInTheDocument();
    expect(screen.getByTestId('icon-check')).toBeInTheDocument();

    act(() => {
      vi.advanceTimersByTime(600);
    });
    expect(screen.getByRole('button', { name: 'Copy execution id' })).toBeInTheDocument();
  });

  it('does not propagate the click to ancestors', () => {
    Object.assign(navigator, { clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    const parentClick = vi.fn();

    render(
      <div onClick={parentClick}>
        <CopyButton value="x" label="thing" />
      </div>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Copy thing' }));

    expect(parentClick).not.toHaveBeenCalled();
  });

  it('renders the optional caption and swaps it for "Copied"', async () => {
    Object.assign(navigator, { clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });

    render(<CopyButton value="x" label="path" caption="Copy path" />);
    expect(screen.getByText('Copy path')).toBeInTheDocument();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Copy path' }));
      await Promise.resolve();
    });

    expect(screen.getByText('Copied')).toBeInTheDocument();
  });
});

describe('copyToClipboard', () => {
  it('falls back to execCommand when the Clipboard API is unavailable', async () => {
    const original = navigator.clipboard;
    Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true });
    const execCommand = vi.fn().mockReturnValue(true);
    Object.assign(document, { execCommand });

    try {
      await copyToClipboard('fallback-text');
      expect(execCommand).toHaveBeenCalledWith('copy');
      expect(document.querySelector('textarea')).toBeNull();
    } finally {
      Object.defineProperty(navigator, 'clipboard', { value: original, configurable: true });
    }
  });
});