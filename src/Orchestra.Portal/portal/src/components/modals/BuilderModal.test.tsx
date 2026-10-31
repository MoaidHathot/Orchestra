import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import BuilderModal from './BuilderModal';

describe('Visual Builder preview', () => {
  it('clearly identifies incomplete functionality and supported step types', () => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
    render(<BuilderModal open onClose={() => {}} onSave={() => {}} />);
    expect(screen.getByRole('note')).toHaveTextContent('Visual Builder — Preview');
    expect(screen.getByRole('note')).toHaveTextContent('functionality is incomplete');
    expect(screen.getByRole('note')).toHaveTextContent('Prompt, HTTP, and Transform');
    vi.unstubAllGlobals();
  });
});
import '@testing-library/jest-dom/vitest';
