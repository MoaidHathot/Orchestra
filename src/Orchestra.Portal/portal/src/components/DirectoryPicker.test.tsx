import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import DirectoryPicker from './DirectoryPicker';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
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

  it('lets a user recover from an invalid path inside the picker', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => url.includes('missing')
      ? new Response(JSON.stringify({ error: 'Directory not found' }), { status: 400 })
      : new Response(JSON.stringify({ currentDirectory: '/work', entries: [] }))));
    render(<DirectoryPicker initialPath="/missing" onSelect={() => {}} onCancel={() => {}} />);
    expect(await screen.findByRole('alert')).toHaveTextContent('Directory not found');
    fireEvent.change(screen.getByLabelText('Folder path'), { target: { value: '/work' } });
    fireEvent.click(screen.getByRole('button', { name: 'Go' }));
    expect(await screen.findByRole('button', { name: 'Use this folder' })).toBeEnabled();
  });
});
import '@testing-library/jest-dom/vitest';
