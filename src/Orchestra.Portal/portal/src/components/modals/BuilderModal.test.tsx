import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import BuilderModal from './BuilderModal';

describe('Visual Builder preview', () => {
  it('clearly identifies incomplete functionality and supported step types', () => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
    render(<BuilderModal open onClose={() => {}} onSave={() => {}} />);
    expect(screen.getByRole('note')).toHaveTextContent('Visual Builder — Preview');
    expect(screen.getByRole('note')).toHaveTextContent('functionality is incomplete');
    expect(screen.getByRole('note')).toHaveTextContent('Prompt, Decision, HTTP, and Transform');
    vi.unstubAllGlobals();
  });

  it('exports typed decisions without inheriting the chat model and keeps invalid edits editable', () => {
    const save = vi.fn();
    render(<BuilderModal open onClose={() => {}} onSave={save} />);
    fireEvent.click(screen.getByRole('button', { name: 'Decision' }));
    const questions = screen.getByLabelText('Questions (JSON array)');
    const submit = screen.getByRole('button', { name: 'Save workflow' });
    fireEvent.change(questions, { target: { value: '[' } });
    expect(submit).toBeDisabled();
    const rubric = [{ kind: 'boolean', name: 'verified', instructions: 'Is the order verified?', threshold: 0.9 }];
    fireEvent.change(questions, { target: { value: JSON.stringify(rubric) } });
    fireEvent.change(screen.getByLabelText('Context'), { target: { value: '{{source.output}}' } });
    fireEvent.change(screen.getByLabelText('Context format'), { target: { value: 'json' } });
    fireEvent.click(submit);
    const step = JSON.parse(save.mock.calls[0][0]).steps[0];
    expect(step).toMatchObject({ type: 'Decision', context: '{{source.output}}', contextFormat: 'json', questions: rubric });
    expect(step.model).toBeUndefined();
  });
});
import '@testing-library/jest-dom/vitest';
