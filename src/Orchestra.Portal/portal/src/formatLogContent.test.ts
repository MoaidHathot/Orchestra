import { describe, it, expect } from 'vitest';
import { formatLogContent } from './formatLogContent';

describe('formatLogContent - step-process', () => {
  it('renders the process id, executable and arguments', () => {
    expect(
      formatLogContent({
        type: 'step-process',
        processId: 31108,
        fileName: 'pwsh',
        arguments: '-NoProfile -File C:\\Temp\\orchestra-x.ps1',
      }),
    ).toBe('Process started (pid 31108): pwsh -NoProfile -File C:\\Temp\\orchestra-x.ps1');
  });

  it('degrades gracefully without pid or arguments', () => {
    expect(formatLogContent({ type: 'step-process', fileName: 'cmd.exe' })).toBe('Process started: cmd.exe');
  });
});