import { useEffect } from 'react';

interface KeyboardShortcutsOptions {
  onEscape?: () => void;
}

/**
 * Registers global keyboard shortcuts.
 * - Escape: calls onEscape callback (e.g., close sidebar/modal)
 */
export function useKeyboardShortcuts({ onEscape }: KeyboardShortcutsOptions): void {
  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && onEscape) {
        onEscape();
      }
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onEscape]);
}
