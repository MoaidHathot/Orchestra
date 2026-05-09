import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import MarkdownContent from './MarkdownContent';

describe('MarkdownContent', () => {
  it('renders common markdown elements for human-readable output', () => {
    const markdown = [
      '# Summary',
      '',
      '- **Result:** done',
      '- Plain line',
      '',
      '| Name | Value |',
      '| --- | --- |',
      '| status | ok |',
      '',
      '```json',
      '{"ok":true}',
      '```',
    ].join('\n');

    const { container } = render(<MarkdownContent markdown={markdown} />);

    expect(screen.getByRole('heading', { name: 'Summary', level: 1 })).toBeInTheDocument();
    expect(screen.getByText('Result:')).toBeInTheDocument();
    expect(screen.getByRole('table')).toBeInTheDocument();
    expect(container.querySelector('code.language-json')).toHaveTextContent('{"ok":true}');
  });

  it('supports single newlines for streamed model output', () => {
    const { container } = render(<MarkdownContent markdown={'line one\nline two'} />);

    expect(container).toHaveTextContent(/line one\s+line two/);
    expect(container.querySelector('br')).toBeInTheDocument();
  });

  it('does not render raw html from model output', () => {
    const { container } = render(
      <MarkdownContent markdown={'<img src=x onerror="alert(1)">\n\n<script>alert(1)</script>\n\n**safe**'} />,
    );

    expect(container.querySelector('img')).not.toBeInTheDocument();
    expect(container.querySelector('script')).not.toBeInTheDocument();
    expect(screen.getByText('safe')).toBeInTheDocument();
  });
});
