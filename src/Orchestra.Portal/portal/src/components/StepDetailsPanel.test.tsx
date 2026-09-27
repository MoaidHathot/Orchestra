import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import StepDetailsPanel from './StepDetailsPanel';
import type { Step } from '../types';
import { describeOutputCap, isOutputTruncated, formatBytes } from '../utils';

describe('StepDetailsPanel output cap', () => {
  it('shows the engine default for a Command step that sets no maxOutputBytes', () => {
    const step: Step = { name: 'build', type: 'Command', command: 'dotnet', arguments: ['build'] };
    render(<StepDetailsPanel step={step} />);

    expect(screen.getByTestId('output-cap')).toHaveTextContent('10.0 MB (default)');
  });

  it('shows an explicit cap and "unlimited" for 0', () => {
    const { rerender } = render(<StepDetailsPanel step={{ name: 'h', type: 'Http', url: 'https://x', maxOutputBytes: 4096 }} />);
    expect(screen.getByTestId('output-cap')).toHaveTextContent('4.0 KB');

    rerender(<StepDetailsPanel step={{ name: 's', type: 'Script', maxOutputBytes: 0 }} />);
    expect(screen.getByTestId('output-cap')).toHaveTextContent('unlimited');
  });

  it('is absent for step types that do not capture output', () => {
    render(<StepDetailsPanel step={{ name: 'p', type: 'Prompt', model: 'claude-opus-4.8' }} />);

    expect(screen.queryByTestId('output-cap')).not.toBeInTheDocument();
    expect(screen.queryByText(/Other Properties/)).not.toBeInTheDocument();
  });
});

describe('output helpers', () => {
  it('describeOutputCap', () => {
    expect(describeOutputCap(undefined, false)).toBeNull();
    expect(describeOutputCap(undefined, true)).toBe('10.0 MB (default)');
    expect(describeOutputCap(0, true)).toBe('unlimited');
    expect(describeOutputCap(2048, true)).toBe('2.0 KB');
  });

  it('formatBytes', () => {
    expect(formatBytes(null)).toBeNull();
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(10 * 1024 * 1024)).toBe('10.0 MB');
  });

  it('isOutputTruncated recognises the engine marker only', () => {
    expect(isOutputTruncated('line 1\n[output truncated: exceeded maxOutputBytes (2,048 bytes); 18,000 bytes dropped]')).toBe(true);
    expect(isOutputTruncated('the word truncated appears but no marker')).toBe(false);
    expect(isOutputTruncated(null)).toBe(false);
  });
});
