import { describe, it, expect } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import '@testing-library/jest-dom';
import SubagentCard from './SubagentCard';
import type { ActorStream, ActorContext } from '../types';

// ── Helpers ──────────────────────────────────────────────────────────────────

function makeMainStream(overrides: Partial<ActorStream> = {}): ActorStream {
  return {
    key: 'main',
    actor: null,
    content: '',
    reasoning: '',
    events: [],
    startedAt: new Date().toISOString(),
    status: 'running',
    ...overrides,
  };
}

function makeActor(overrides: Partial<ActorContext> = {}): ActorContext {
  return {
    agentName: 'writer',
    displayName: 'Writer Agent',
    toolCallId: 'tc-1',
    depth: 1,
    ...overrides,
  };
}

function makeSubStream(overrides: Partial<ActorStream> = {}): ActorStream {
  return {
    key: 'tc-1',
    actor: makeActor(),
    content: '',
    reasoning: '',
    events: [],
    startedAt: new Date().toISOString(),
    status: 'running',
    ...overrides,
  };
}

// ── Tests ────────────────────────────────────────────────────────────────────

describe('SubagentCard', () => {
  describe('main agent rendering', () => {
    it('renders the main-agent label and no depth pill', () => {
      render(<SubagentCard stream={makeMainStream({ content: 'hello' })} />);
      expect(screen.getByText('Main agent')).toBeInTheDocument();
      expect(screen.queryByTitle('Nesting depth')).not.toBeInTheDocument();
    });

    it('shows live response content when expanded', () => {
      render(<SubagentCard stream={makeMainStream({ content: 'streamed text' })} />);
      expect(screen.getByText('streamed text')).toBeInTheDocument();
      expect(screen.getByText('Response')).toBeInTheDocument();
    });

    it('shows the empty placeholder when there is no output yet', () => {
      render(<SubagentCard stream={makeMainStream()} />);
      expect(screen.getByText('Waiting for output…')).toBeInTheDocument();
    });
  });

  describe('sub-agent rendering', () => {
    it('renders the sub-agent display name', () => {
      render(<SubagentCard stream={makeSubStream()} />);
      expect(screen.getByText('Writer Agent')).toBeInTheDocument();
    });

    it('falls back to agentName when displayName is absent', () => {
      const stream = makeSubStream({
        actor: makeActor({ displayName: undefined }),
      });
      render(<SubagentCard stream={stream} />);
      expect(screen.getByText('writer')).toBeInTheDocument();
    });

    it('shows depth pill when depth > 1', () => {
      const stream = makeSubStream({
        actor: makeActor({ depth: 2 }),
      });
      render(<SubagentCard stream={stream} />);
      expect(screen.getByTitle('Nesting depth')).toHaveTextContent('depth 2');
    });

    it('does not show depth pill when depth is 1', () => {
      render(<SubagentCard stream={makeSubStream()} />);
      expect(screen.queryByTitle('Nesting depth')).not.toBeInTheDocument();
    });

    it('applies left-margin indentation based on depth', () => {
      const { container } = render(
        <SubagentCard stream={makeSubStream({ actor: makeActor({ depth: 2 }) })} />,
      );
      const card = container.querySelector('.subagent-card') as HTMLElement;
      expect(card.style.marginLeft).toBe('32px');
    });
  });

  describe('status and lifecycle', () => {
    it('renders the running status badge by default', () => {
      render(<SubagentCard stream={makeSubStream()} />);
      expect(screen.getByText('running')).toBeInTheDocument();
    });

    it('renders failed status with an error message when expanded', () => {
      render(
        <SubagentCard
          stream={makeSubStream({
            status: 'failed',
            errorMessage: 'boom',
            completedAt: new Date().toISOString(),
          })}
        />,
      );
      // Failed cards start collapsed; status badge is always visible.
      expect(screen.getByText('failed')).toBeInTheDocument();
      // Expand to surface the error body.
      fireEvent.click(screen.getByText('Writer Agent'));
      expect(screen.getByText('boom')).toBeInTheDocument();
    });

    it('auto-collapses when status transitions from running to completed', () => {
      const { rerender } = render(<SubagentCard stream={makeSubStream({ content: 'x' })} />);
      // Initially expanded → response is visible.
      expect(screen.getByText('Response')).toBeInTheDocument();

      // Simulate lifecycle completion.
      act(() => {
        rerender(
          <SubagentCard
            stream={makeSubStream({
              content: 'x',
              status: 'completed',
              completedAt: new Date().toISOString(),
            })}
          />,
        );
      });

      // Body should no longer render the Response section.
      expect(screen.queryByText('Response')).not.toBeInTheDocument();
    });

    it('user can re-expand a collapsed completed card', () => {
      const completed = makeSubStream({
        content: 'final',
        status: 'completed',
        completedAt: new Date().toISOString(),
      });
      // Render directly as completed → starts collapsed (isRunning is false).
      render(<SubagentCard stream={completed} />);
      expect(screen.queryByText('Response')).not.toBeInTheDocument();

      // Click the header to expand.
      fireEvent.click(screen.getByText('Writer Agent'));
      expect(screen.getByText('Response')).toBeInTheDocument();
      expect(screen.getByText('final')).toBeInTheDocument();
    });
  });

  describe('reasoning subsection', () => {
    it('renders a collapsed-by-default reasoning toggle when reasoning is present', () => {
      render(
        <SubagentCard
          stream={makeSubStream({ reasoning: 'thinking about the answer' })}
        />,
      );
      // Toggle button is visible…
      expect(screen.getByText(/Reasoning \(\d+ chars\)/)).toBeInTheDocument();
      // …but the reasoning text itself is not rendered yet.
      expect(screen.queryByText('thinking about the answer')).not.toBeInTheDocument();
    });

    it('expands reasoning when the toggle is clicked', () => {
      render(<SubagentCard stream={makeSubStream({ reasoning: 'because reasons' })} />);
      fireEvent.click(screen.getByText(/Reasoning \(/));
      expect(screen.getByText('because reasons')).toBeInTheDocument();
    });

    it('omits the reasoning subsection entirely when reasoning is empty', () => {
      render(<SubagentCard stream={makeSubStream({ content: 'x' })} />);
      expect(screen.queryByText(/Reasoning \(/)).not.toBeInTheDocument();
    });
  });

  describe('events list', () => {
    it('renders tool/event rows when events are present', () => {
      const stream = makeSubStream({
        events: [
          { type: 'tool-started', toolName: 'read_file' } as never,
          { type: 'tool-completed', toolName: 'read_file' } as never,
        ],
      });
      render(<SubagentCard stream={stream} />);
      expect(screen.getByText(/Tool calls & events \(2\)/)).toBeInTheDocument();
      expect(screen.getAllByText('read_file').length).toBeGreaterThan(0);
    });
  });

  describe('header interaction', () => {
    it('clicking the header toggles expansion', () => {
      render(<SubagentCard stream={makeSubStream({ content: 'x' })} />);
      const header = screen.getByText('Writer Agent');
      // Starts expanded.
      expect(screen.getByText('Response')).toBeInTheDocument();
      fireEvent.click(header);
      expect(screen.queryByText('Response')).not.toBeInTheDocument();
      fireEvent.click(header);
      expect(screen.getByText('Response')).toBeInTheDocument();
    });
  });
});
