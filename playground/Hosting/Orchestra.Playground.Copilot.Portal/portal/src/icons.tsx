import React from 'react';

export const Icons = {
  Search: (): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor">
      <path d="M11.5 7a4.5 4.5 0 1 1-9 0 4.5 4.5 0 0 1 9 0Zm-.82 4.74a6 6 0 1 1 1.06-1.06l3.04 3.04-1.06 1.06-3.04-3.04Z" />
    </svg>
  ),
  Play: (): React.JSX.Element => (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
      <path d="M4 2l10 6-10 6V2z" />
    </svg>
  ),
  Eye: (): React.JSX.Element => (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
      <path d="M8 3c-3.5 0-6.5 2.5-7.5 5 1 2.5 4 5 7.5 5s6.5-2.5 7.5-5c-1-2.5-4-5-7.5-5zm0 8a3 3 0 1 1 0-6 3 3 0 0 1 0 6z" />
    </svg>
  ),
  History: (): React.JSX.Element => (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
      <path d="M8 1a7 7 0 1 0 0 14A7 7 0 0 0 8 1zm0 12.5a5.5 5.5 0 1 1 0-11 5.5 5.5 0 0 1 0 11zM8.5 4v4l3 1.5-.5 1-3.5-1.75V4h1z" />
    </svg>
  ),
  Plus: (): React.JSX.Element => (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
      <path d="M8 2v12M2 8h12" stroke="currentColor" strokeWidth="2" fill="none" />
    </svg>
  ),
  X: (): React.JSX.Element => (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
      <path d="M4 4l8 8M12 4l-8 8" stroke="currentColor" strokeWidth="2" />
    </svg>
  ),
  Check: (): React.JSX.Element => (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
      <path d="M13.5 4l-7 7L3 7.5" stroke="currentColor" strokeWidth="2" fill="none" />
    </svg>
  ),
  Folder: (): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor">
      <path d="M1 3.5A1.5 1.5 0 0 1 2.5 2h3.172a1.5 1.5 0 0 1 1.06.44l.672.672a1.5 1.5 0 0 0 1.06.44H13.5A1.5 1.5 0 0 1 15 5v7a1.5 1.5 0 0 1-1.5 1.5h-11A1.5 1.5 0 0 1 1 12V3.5z" />
    </svg>
  ),
  File: (): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor">
      <path d="M4 1h5l4 4v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V2a1 1 0 0 1 1-1z" />
    </svg>
  ),
  Clock: (): React.JSX.Element => (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
      <path d="M8 1a7 7 0 1 0 0 14A7 7 0 0 0 8 1zm-.5 3.5v4l3 1.5-.5 1-3.5-1.75V4.5h1z" />
    </svg>
  ),
  Steps: (): React.JSX.Element => (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
      <path d="M2 3h12v2H2zM2 7h12v2H2zM2 11h12v2H2z" />
    </svg>
  ),
  Workflow: (): React.JSX.Element => (
    <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
      <path d="M6 2h4v4H6zM1 10h4v4H1zM11 10h4v4h-4zM8 6v4M3 10V8h10v2" />
    </svg>
  ),
  Tool: (): React.JSX.Element => (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
      <path d="M14.7 5.7l-2.8 2.8-.9-.9-2.8 2.8L4 6.2 1.3 8.9.4 8 3.2 5.2l1.2 1.2 2.8-2.8-.9-.9L9.1.9a3.2 3.2 0 0 1 4.5 0l1.1 1.1a3.2 3.2 0 0 1 0 4.5l.9.2-1 1z" />
    </svg>
  ),
  Terminal: (): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor">
      <path d="M0 2.75C0 1.784.784 1 1.75 1h12.5c.966 0 1.75.784 1.75 1.75v10.5A1.75 1.75 0 0 1 14.25 15H1.75A1.75 1.75 0 0 1 0 13.25Zm1.75-.25a.25.25 0 0 0-.25.25v10.5c0 .138.112.25.25.25h12.5a.25.25 0 0 0 .25-.25V2.75a.25.25 0 0 0-.25-.25ZM7.25 8a.749.749 0 0 1-.22.53l-2.25 2.25a.749.749 0 1 1-1.06-1.06L5.44 8 3.72 6.28a.749.749 0 1 1 1.06-1.06l2.25 2.25c.141.14.22.331.22.53Zm1.5 1.5h3a.75.75 0 0 1 0 1.5h-3a.75.75 0 0 1 0-1.5Z" />
    </svg>
  ),
  Activity: (): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor">
      <path d="M6 2a.75.75 0 0 1 .696.471L10 10.731l1.304-3.26A.751.751 0 0 1 12 7h3.25a.75.75 0 0 1 0 1.5h-2.742l-1.812 4.528a.751.751 0 0 1-1.392 0L6 4.77 4.696 8.029A.75.75 0 0 1 4 8.5H.75a.75.75 0 0 1 0-1.5h2.742l1.812-4.528A.751.751 0 0 1 6 2Z" />
    </svg>
  ),
  Spinner: (): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor" className="spin">
      <path d="M8 0a8 8 0 1 0 8 8h-2a6 6 0 1 1-6-6V0Z" />
    </svg>
  ),
  Copy: (): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor">
      <path d="M0 6.75C0 5.784.784 5 1.75 5h1.5a.75.75 0 0 1 0 1.5h-1.5a.25.25 0 0 0-.25.25v7.5c0 .138.112.25.25.25h7.5a.25.25 0 0 0 .25-.25v-1.5a.75.75 0 0 1 1.5 0v1.5A1.75 1.75 0 0 1 9.25 16h-7.5A1.75 1.75 0 0 1 0 14.25ZM5 1.75C5 .784 5.784 0 6.75 0h7.5C15.216 0 16 .784 16 1.75v7.5A1.75 1.75 0 0 1 14.25 11h-7.5A1.75 1.75 0 0 1 5 9.25Zm1.75-.25a.25.25 0 0 0-.25.25v7.5c0 .138.112.25.25.25h7.5a.25.25 0 0 0 .25-.25v-7.5a.25.25 0 0 0-.25-.25Z" />
    </svg>
  ),
  Download: (): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor">
      <path d="M2.75 14A1.75 1.75 0 0 1 1 12.25v-2.5a.75.75 0 0 1 1.5 0v2.5c0 .138.112.25.25.25h10.5a.25.25 0 0 0 .25-.25v-2.5a.75.75 0 0 1 1.5 0v2.5A1.75 1.75 0 0 1 13.25 14Zm-1.97-7.22a.749.749 0 0 1 0-1.06l5.25-5.25a.749.749 0 0 1 1.06 0l5.25 5.25a.749.749 0 1 1-1.06 1.06L8.5 4.06v6.19a.75.75 0 0 1-1.5 0V4.06L4.28 6.78a.749.749 0 0 1-1.06 0Z" />
    </svg>
  ),
  Lock: (): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor">
      <path d="M4 4a4 4 0 0 1 8 0v2h.25c.966 0 1.75.784 1.75 1.75v5.5A1.75 1.75 0 0 1 12.25 15h-8.5A1.75 1.75 0 0 1 2 13.25v-5.5C2 6.784 2.784 6 3.75 6H4Zm8.25 3.5h-8.5a.25.25 0 0 0-.25.25v5.5c0 .138.112.25.25.25h8.5a.25.25 0 0 0 .25-.25v-5.5a.25.25 0 0 0-.25-.25ZM10.5 6V4a2.5 2.5 0 1 0-5 0v2Z" />
    </svg>
  ),
  Sparkles: (): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor">
      <path d="M7.53 1.282a.5.5 0 0 1 .94 0l.478 1.306a7.5 7.5 0 0 0 4.464 4.464l1.305.478a.5.5 0 0 1 0 .94l-1.305.478a7.5 7.5 0 0 0-4.464 4.464l-.478 1.306a.5.5 0 0 1-.94 0l-.478-1.306a7.5 7.5 0 0 0-4.464-4.464L1.282 8.47a.5.5 0 0 1 0-.94l1.306-.478a7.5 7.5 0 0 0 4.464-4.464Z" />
    </svg>
  ),
  Mail: (): React.JSX.Element => (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
      <path d="M1.75 2h12.5c.966 0 1.75.784 1.75 1.75v8.5A1.75 1.75 0 0 1 14.25 14H1.75A1.75 1.75 0 0 1 0 12.25v-8.5C0 2.784.784 2 1.75 2ZM1.5 12.251c0 .138.112.25.25.25h12.5a.25.25 0 0 0 .25-.25V5.809L8.38 9.397a.75.75 0 0 1-.76 0L1.5 5.809v6.442Zm13-8.181v-.32a.25.25 0 0 0-.25-.25H1.75a.25.25 0 0 0-.25.25v.32L8 7.88l6.5-3.81Z" />
    </svg>
  ),
  Menu: (): React.JSX.Element => (
    <svg width="18" height="18" viewBox="0 0 16 16" fill="currentColor">
      <path d="M1 2.75A.75.75 0 0 1 1.75 2h12.5a.75.75 0 0 1 0 1.5H1.75A.75.75 0 0 1 1 2.75Zm0 5A.75.75 0 0 1 1.75 7h12.5a.75.75 0 0 1 0 1.5H1.75A.75.75 0 0 1 1 7.75ZM1.75 12h12.5a.75.75 0 0 1 0 1.5H1.75a.75.75 0 0 1 0-1.5Z" />
    </svg>
  ),
  AlertCircle: (props?: React.SVGProps<SVGSVGElement>): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor" {...props}>
      <path d="M8 1.5a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13ZM0 8a8 8 0 1 1 16 0A8 8 0 0 1 0 8Zm8-3.5a.75.75 0 0 1 .75.75v3.5a.75.75 0 0 1-1.5 0v-3.5A.75.75 0 0 1 8 4.5ZM8 12a1 1 0 1 1 0-2 1 1 0 0 1 0 2Z" />
    </svg>
  ),
  Ban: (): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor">
      <path d="M3.05 3.05a7 7 0 0 0 9.9 9.9L3.05 3.05Zm1.41-1.41 9.9 9.9a7 7 0 0 0-9.9-9.9ZM0 8a8 8 0 1 1 16 0A8 8 0 0 1 0 8Z" />
    </svg>
  ),
  SkipForward: (): React.JSX.Element => (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
      <path d="M2 2l8 6-8 6V2z" />
      <path d="M12 2v12" stroke="currentColor" strokeWidth="2" />
    </svg>
  ),
  Filter: (): React.JSX.Element => (
    <svg width="12" height="12" viewBox="0 0 16 16" fill="currentColor">
      <path d="M.75 3h14.5a.75.75 0 0 1 0 1.5H.75a.75.75 0 0 1 0-1.5ZM3 7.25h10a.75.75 0 0 1 0 1.5H3a.75.75 0 0 1 0-1.5Zm2.75 4.25h4.5a.75.75 0 0 1 0 1.5h-4.5a.75.75 0 0 1 0-1.5Z" />
    </svg>
  ),
  WifiOff: (props?: React.SVGProps<SVGSVGElement>): React.JSX.Element => (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" {...props}>
      <line x1="1" y1="1" x2="23" y2="23" />
      <path d="M16.72 11.06A10.94 10.94 0 0 1 19 12.55" />
      <path d="M5 12.55a10.94 10.94 0 0 1 5.17-2.39" />
      <path d="M10.71 5.05A16 16 0 0 1 22.56 9" />
      <path d="M1.42 9a15.91 15.91 0 0 1 4.7-2.88" />
      <path d="M8.53 16.11a6 6 0 0 1 6.95 0" />
      <line x1="12" y1="20" x2="12.01" y2="20" />
    </svg>
  ),
};

export function getTriggerIcon(triggeredBy: string | undefined): React.JSX.Element {
  switch (triggeredBy) {
    case 'manual':
      return <Icons.Play />;
    case 'scheduler':
      return <Icons.Clock />;
    case 'loop':
      return <Icons.Steps />;
    case 'webhook':
      return <Icons.Tool />;
    case 'email':
      return <Icons.Mail />;
    default:
      return <Icons.Play />;
  }
}
