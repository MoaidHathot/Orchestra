import { useEffect, useRef, useState } from 'react';
import { Icons } from '../icons';

/**
 * Copies `text` to the clipboard. Prefers the async Clipboard API and falls back to a
 * hidden textarea + execCommand for insecure contexts (plain-http hosts) where
 * `navigator.clipboard` is unavailable.
 */
export async function copyToClipboard(text: string): Promise<void> {
  if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(text);
    return;
  }
  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.setAttribute('readonly', '');
  textarea.style.position = 'fixed';
  textarea.style.opacity = '0';
  document.body.appendChild(textarea);
  textarea.select();
  try {
    document.execCommand('copy');
  } finally {
    document.body.removeChild(textarea);
  }
}

export interface CopyButtonProps {
  /** Exact text placed on the clipboard. */
  value: string;
  /** What is being copied, used for the tooltip / accessible name ("Copy execution id"). */
  label: string;
  /** Optional visible caption rendered next to the icon. */
  caption?: string;
  className?: string;
  /** How long the "copied" confirmation stays visible, in ms. */
  feedbackMs?: number;
}

/**
 * Small icon button that copies a value and briefly confirms it. Click events do not bubble,
 * so it can sit inside clickable cards/rows without triggering their handlers.
 */
export default function CopyButton({ value, label, caption, className, feedbackMs = 1500 }: CopyButtonProps): React.JSX.Element {
  const [copied, setCopied] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
    },
    [],
  );

  const handleClick = (e: React.MouseEvent): void => {
    e.stopPropagation();
    e.preventDefault();
    void copyToClipboard(value)
      .then(() => {
        setCopied(true);
        if (timer.current) clearTimeout(timer.current);
        timer.current = setTimeout(() => setCopied(false), feedbackMs);
      })
      .catch(() => {
        /* clipboard denied - nothing sensible to do beyond leaving the value visible */
      });
  };

  const title = copied ? 'Copied' : `Copy ${label}`;
  return (
    <button
      type="button"
      className={`copy-button${copied ? ' copy-button-copied' : ''}${className ? ` ${className}` : ''}`}
      onClick={handleClick}
      title={title}
      aria-label={title}
      data-copied={copied ? 'true' : undefined}
    >
      {copied ? <Icons.Check /> : <Icons.Copy />}
      {caption && <span className="copy-button-caption">{copied ? 'Copied' : caption}</span>}
    </button>
  );
}
