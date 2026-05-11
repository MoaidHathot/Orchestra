import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import HistoryFilterSelector from './HistoryFilterSelector';
import {
  type HistoryFilterState,
  DEFAULT_FILTER_STATE,
  ALL_RUN_ORIGINS,
  ALL_RUN_STATUS_FILTERS,
} from '../runFilters';

function defaultState(): HistoryFilterState {
  return {
    scope: DEFAULT_FILTER_STATE.scope,
    origins: [...ALL_RUN_ORIGINS],
    statuses: [...ALL_RUN_STATUS_FILTERS],
    hideIncomplete: DEFAULT_FILTER_STATE.hideIncomplete,
  };
}

describe('HistoryFilterSelector', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('renders a closed trigger by default', () => {
    render(<HistoryFilterSelector state={defaultState()} onChange={vi.fn()} />);

    const trigger = screen.getByRole('button', { name: /history filters/i });
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    // The default state shows "completed" because hideIncomplete is true by default.
    expect(trigger).toHaveTextContent('completed');
  });

  it('opens the dropdown on trigger click', () => {
    render(<HistoryFilterSelector state={defaultState()} onChange={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: /history filters/i }));
    expect(screen.getByRole('dialog', { name: /history filters/i })).toBeInTheDocument();
  });

  it('closes on Escape', () => {
    render(<HistoryFilterSelector state={defaultState()} onChange={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: /history filters/i }));
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('selecting a scope radio fires onChange with the new scope', () => {
    const onChange = vi.fn();
    const initial = defaultState();
    render(<HistoryFilterSelector state={initial} onChange={onChange} />);

    fireEvent.click(screen.getByRole('button', { name: /history filters/i }));
    fireEvent.click(screen.getByLabelText('Top-level only'));

    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ scope: 'roots' }));
  });

  it('toggling an origin checkbox removes it from the allow-list', () => {
    const onChange = vi.fn();
    const initial = defaultState();
    render(<HistoryFilterSelector state={initial} onChange={onChange} />);

    fireEvent.click(screen.getByRole('button', { name: /history filters/i }));
    // The Manual origin is rendered with its capital-cased label.
    fireEvent.click(screen.getByLabelText('Manual'));

    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({
      origins: expect.not.arrayContaining(['manual']),
    }));
  });

  it('toggling a status checkbox removes it from the allow-list', () => {
    const onChange = vi.fn();
    render(<HistoryFilterSelector state={defaultState()} onChange={onChange} />);

    fireEvent.click(screen.getByRole('button', { name: /history filters/i }));
    fireEvent.click(screen.getByLabelText('Failed'));

    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({
      statuses: expect.not.arrayContaining(['Failed']),
    }));
  });

  it('toggling Hide incomplete fires onChange with the inverted boolean', () => {
    const onChange = vi.fn();
    render(<HistoryFilterSelector state={defaultState()} onChange={onChange} />);

    fireEvent.click(screen.getByRole('button', { name: /history filters/i }));
    fireEvent.click(screen.getByLabelText('Hide incomplete'));

    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ hideIncomplete: false }));
  });

  it('"Reset to defaults" appears only when filters deviate from defaults and resets state', () => {
    const onChange = vi.fn();
    const customized: HistoryFilterState = {
      scope: 'roots',
      origins: ['manual'],
      statuses: ['Failed'],
      hideIncomplete: false,
    };
    const { rerender } = render(<HistoryFilterSelector state={customized} onChange={onChange} />);

    fireEvent.click(screen.getByRole('button', { name: /history filters/i }));
    expect(screen.getByText('Reset to defaults')).toBeInTheDocument();

    fireEvent.click(screen.getByText('Reset to defaults'));
    expect(onChange).toHaveBeenCalledWith(DEFAULT_FILTER_STATE);

    // When state matches defaults, the button is hidden.
    rerender(<HistoryFilterSelector state={defaultState()} onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: /history filters/i }));
    expect(screen.queryByText('Reset to defaults')).not.toBeInTheDocument();
  });

  it('section "All/None" toggle flips selection of all origins', () => {
    const onChange = vi.fn();
    render(<HistoryFilterSelector state={defaultState()} onChange={onChange} />);

    fireEvent.click(screen.getByRole('button', { name: /history filters/i }));
    // When all origins selected the section header shows "None" (clear-all action).
    const dialog = screen.getByRole('dialog');
    const noneButtons = dialog.querySelectorAll('.history-filter-section-action');
    expect(noneButtons.length).toBeGreaterThanOrEqual(2);

    // Click the first "None" — it belongs to the Origins section per render order.
    fireEvent.click(noneButtons[0]);
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ origins: [] }));
  });

  it('shows an active dot when filters deviate from defaults', () => {
    const customized: HistoryFilterState = {
      scope: 'roots',
      origins: [...ALL_RUN_ORIGINS],
      statuses: [...ALL_RUN_STATUS_FILTERS],
      hideIncomplete: true,
    };

    const { container } = render(<HistoryFilterSelector state={customized} onChange={vi.fn()} />);
    expect(container.querySelector('.history-filter-active-dot')).toBeInTheDocument();
  });

  it('hides the active dot when filters are at defaults', () => {
    const { container } = render(<HistoryFilterSelector state={defaultState()} onChange={vi.fn()} />);

    expect(container.querySelector('.history-filter-active-dot')).not.toBeInTheDocument();
  });

  it('summary text reflects scope, narrowed origins/statuses, and hideIncomplete', () => {
    const partial: HistoryFilterState = {
      scope: 'children',
      origins: ['manual', 'scheduler'],
      statuses: ['Failed'],
      hideIncomplete: false,
    };
    render(<HistoryFilterSelector state={partial} onChange={vi.fn()} />);

    const trigger = screen.getByRole('button', { name: /history filters/i });
    expect(trigger).toHaveTextContent('Children');
    expect(trigger).toHaveTextContent('2 origins');
    expect(trigger).toHaveTextContent('1 statuses');
    // hideIncomplete=false should not contribute "completed" badge to the trigger summary.
    expect(trigger.textContent).not.toContain('completed');
  });

  it('renders the Show all executions footer button when onShowAllRequested is provided', () => {
    const onShowAllRequested = vi.fn();
    render(
      <HistoryFilterSelector
        state={defaultState()}
        onChange={vi.fn()}
        onShowAllRequested={onShowAllRequested}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /history filters/i }));

    const showAll = screen.getByRole('button', { name: /show all executions/i });
    expect(showAll).toBeInTheDocument();

    fireEvent.click(showAll);
    expect(onShowAllRequested).toHaveBeenCalledTimes(1);
    // Clicking the footer button also closes the dropdown.
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('omits the Show all executions footer button when onShowAllRequested is missing', () => {
    render(<HistoryFilterSelector state={defaultState()} onChange={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: /history filters/i }));
    expect(screen.queryByRole('button', { name: /show all executions/i })).not.toBeInTheDocument();
  });
});
