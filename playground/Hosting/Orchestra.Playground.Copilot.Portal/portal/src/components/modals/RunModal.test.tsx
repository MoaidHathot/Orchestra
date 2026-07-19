import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import RunModal from './RunModal';
import type { Orchestration, InputDefinition } from '../../types';

/**
 * Stub the focus-trap hook -- it pulls in browser focus APIs that jsdom only
 * partly emulates, and the focus behaviour isn't what we're verifying here.
 */
vi.mock('../../hooks/useFocusTrap', () => ({
  useFocusTrap: () => ({ current: null }),
}));

vi.mock('../../icons', () => ({
  Icons: {
    X: () => <span>×</span>,
    Play: () => <span>▶</span>,
  },
}));

function makeOrchestration(inputs?: Record<string, InputDefinition>): Orchestration {
  return {
    id: 'test-orch-001',
    name: 'test-orch',
    description: 'A test orchestration',
    path: '/tmp/test-orch.yaml',
    version: '1.0.0',
    parameters: inputs ? undefined : ['topic', 'depth'],
    inputs: inputs ?? undefined,
    tags: [],
    enabled: true,
    isActive: false,
    runCount: 0,
    triggerType: 'Manual',
    steps: [],
  } as unknown as Orchestration;
}

describe('RunModal', () => {
  describe('initialValues prefill (re-run with edits flow)', () => {
    it('pre-fills typed-input fields from initialValues when the modal opens', () => {
      // Re-run-with-edits seeds the parameter form with the source run's stored
      // parameters so the user can tweak them before submitting. The seeding
      // must happen on open and must NOT clobber user typing later.
      const orch = makeOrchestration({
        topic: { type: 'string', description: 'What to research', required: true },
        depth: { type: 'string', description: 'How deep to go', required: false },
      });

      render(
        <RunModal
          open={true}
          orchestration={orch}
          onClose={vi.fn()}
          onRun={vi.fn()}
          initialValues={{ topic: 'quantum computing', depth: 'shallow' }}
        />,
      );

      expect((screen.getByPlaceholderText('Enter topic') as HTMLInputElement).value)
        .toBe('quantum computing');
      expect((screen.getByPlaceholderText('Enter depth') as HTMLInputElement).value)
        .toBe('shallow');
    });

    it('falls back to empty strings when initialValues is absent (fresh run behavior)', () => {
      const orch = makeOrchestration({
        topic: { type: 'string', description: 'What to research', required: true },
      });

      render(
        <RunModal
          open={true}
          orchestration={orch}
          onClose={vi.fn()}
          onRun={vi.fn()}
        />,
      );

      expect((screen.getByPlaceholderText('Enter topic') as HTMLInputElement).value).toBe('');
    });

    it('pre-fills legacy `parameters[]` fields too, not just typed inputs', () => {
      // Some orchestrations use the older `parameters: [name1, name2]` shape
      // without a typed `inputs` block. Re-run-with-edits should still work.
      const orch = makeOrchestration(); // uses parameters=['topic','depth']

      render(
        <RunModal
          open={true}
          orchestration={orch}
          onClose={vi.fn()}
          onRun={vi.fn()}
          initialValues={{ topic: 'AI', depth: 'deep' }}
        />,
      );

      expect((screen.getByPlaceholderText('Enter topic') as HTMLInputElement).value).toBe('AI');
      expect((screen.getByPlaceholderText('Enter depth') as HTMLInputElement).value).toBe('deep');
    });

    it('ignores initialValues keys that aren\'t in the orchestration\'s input schema', () => {
      // The source run may have parameters that no longer exist in the current
      // schema (e.g., orchestration was edited between runs). They should not
      // render as ghost fields and should not crash the modal.
      const orch = makeOrchestration({
        topic: { type: 'string', description: 'What', required: true },
      });

      render(
        <RunModal
          open={true}
          orchestration={orch}
          onClose={vi.fn()}
          onRun={vi.fn()}
          initialValues={{ topic: 'kept', removedParam: 'gone' }}
        />,
      );

      // The kept value renders; the orphan key is silently dropped.
      expect((screen.getByPlaceholderText('Enter topic') as HTMLInputElement).value).toBe('kept');
      expect(screen.queryByText(/removedParam/)).toBeNull();
    });

    it('only seeds on open, so re-rendering with the same open=true does not clobber user edits', () => {
      // The hook deps are [orchestration, open, initialValues]. Stable references
      // for all three across re-renders must NOT trigger a reseed.
      const orch = makeOrchestration({
        topic: { type: 'string', description: 'What', required: true },
      });
      const seed = { topic: 'initial' };

      const { rerender } = render(
        <RunModal
          open={true}
          orchestration={orch}
          onClose={vi.fn()}
          onRun={vi.fn()}
          initialValues={seed}
        />,
      );
      const input = screen.getByPlaceholderText('Enter topic') as HTMLInputElement;
      expect(input.value).toBe('initial');

      // User edits the field.
      fireEvent.change(input, { target: { value: 'user typed' } });
      expect(input.value).toBe('user typed');

      // Re-render with the SAME orchestration + SAME initialValues reference.
      // The hook must not fire again -- the user's edit must survive.
      rerender(
        <RunModal
          open={true}
          orchestration={orch}
          onClose={vi.fn()}
          onRun={vi.fn()}
          initialValues={seed}
        />,
      );

      expect((screen.getByPlaceholderText('Enter topic') as HTMLInputElement).value).toBe('user typed');
    });
  });

  describe('title and submitLabel overrides', () => {
    it('uses the supplied title in place of the default "Run {name}"', () => {
      const orch = makeOrchestration();
      render(
        <RunModal
          open={true}
          orchestration={orch}
          onClose={vi.fn()}
          onRun={vi.fn()}
          title="Re-run test-orch"
        />,
      );
      expect(screen.getByText('Re-run test-orch')).toBeInTheDocument();
    });

    it('uses the supplied submit label in place of the default "Run"', () => {
      const orch = makeOrchestration();
      render(
        <RunModal
          open={true}
          orchestration={orch}
          onClose={vi.fn()}
          onRun={vi.fn()}
          submitLabel="Re-run"
        />,
      );
      // The button text now reads "▶ Re-run" (the Play icon stub + the label).
      expect(screen.getByRole('button', { name: /Re-run/ })).toBeInTheDocument();
    });
  });

  describe('submit behavior is unchanged by the new props', () => {
    it('strips empty values and calls onRun with the survivors', () => {
      const orch = makeOrchestration({
        topic: { type: 'string', description: 'What', required: true },
        depth: { type: 'string', description: 'How deep', required: false },
      });
      const onRun = vi.fn();
      render(
        <RunModal
          open={true}
          orchestration={orch}
          onClose={vi.fn()}
          onRun={onRun}
          initialValues={{ topic: 'AI', depth: '' }}
        />,
      );

      fireEvent.click(screen.getByRole('button', { name: /Run/ }));

      expect(onRun).toHaveBeenCalledWith({ topic: 'AI' });
    });
  });

  describe('dismissal behavior (no accidental data loss)', () => {
    // Regression guard: this modal holds unsaved run parameters. Clicking the backdrop
    // must NOT discard them — only the explicit Cancel/X/Run controls close the modal.
    // (Escape is disabled via useFocusTrap(open) with no handler; that mechanism is
    // covered directly in hooks/useFocusTrap.test.tsx since this suite mocks the hook.)
    it('does NOT close when the backdrop overlay is clicked', () => {
      const onClose = vi.fn();
      const { container } = render(
        <RunModal open={true} orchestration={makeOrchestration()} onClose={onClose} onRun={vi.fn()} />,
      );
      const overlay = container.querySelector('.modal-overlay');
      expect(overlay).not.toBeNull();
      fireEvent.click(overlay!);
      expect(onClose).not.toHaveBeenCalled();
    });

    it('closes via the Cancel button', () => {
      const onClose = vi.fn();
      render(
        <RunModal open={true} orchestration={makeOrchestration()} onClose={onClose} onRun={vi.fn()} />,
      );
      fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
      expect(onClose).toHaveBeenCalledTimes(1);
    });

    it('closes via the X (Close) button', () => {
      const onClose = vi.fn();
      render(
        <RunModal open={true} orchestration={makeOrchestration()} onClose={onClose} onRun={vi.fn()} />,
      );
      fireEvent.click(screen.getByRole('button', { name: 'Close' }));
      expect(onClose).toHaveBeenCalledTimes(1);
    });
  });
});
