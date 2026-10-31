import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import DirectoryPicker from './DirectoryPicker';

afterEach(() => vi.unstubAllGlobals());
describe('host directory picker', () => {
  it('navigates directories and returns the selected host path', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => new Response(JSON.stringify(url.includes('%2Fwork')
      ? { currentDirectory: '/work', entries: [] }
      : { currentDirectory: '/', entries: [{ name: 'work', path: '/work', isDirectory: true }] }))));
    const select = vi.fn();
    render(<DirectoryPicker onSelect={select} onCancel={() => {}} />);
    fireEvent.click(await screen.findByRole('button', { name: 'work' }));
    await waitFor(() => expect(screen.getByText('/work')).toBeVisible());
    fireEvent.click(screen.getByRole('button', { name: 'Use this folder' }));
    expect(select).toHaveBeenCalledWith('/work');
  });
});
import '@testing-library/jest-dom/vitest';
