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
    expect(container.querySelector('code.language-json')).toHaveTextContent('{ "ok": true }');
  });

  it('formats fenced json code blocks', () => {
    const { container } = render(<MarkdownContent markdown={'```json\n{"ok":true,"items":[1,2]}\n```'} />);

    const code = container.querySelector('code.language-json');
    expect(code).toHaveTextContent(/"ok": true/);
    expect(code).toHaveTextContent(/"items": \[/);
    expect(code?.textContent).toContain('\n  "ok": true,');
    expect(code?.textContent).toContain('\n    1,');
  });

  it('formats json code blocks embedded in result markdown', () => {
    const markdown = [
      '# Self-Healing Run Summary',
      '',
      '## Result',
      '',
      '```json',
      '{"status":"succeeded","parentRunId":"2f1ee17b8dec","attempts":[{"attempt":1,"status":"succeeded"}]}',
      '```',
    ].join('\n');

    const { container } = render(<MarkdownContent markdown={markdown} />);

    const code = container.querySelector('code.language-json');
    expect(screen.getByRole('heading', { name: 'Result', level: 2 })).toBeInTheDocument();
    expect(code?.textContent).toContain('\n  "status": "succeeded",');
    expect(code?.textContent).toContain('\n  "attempts": [');
    expect(code?.textContent).toContain('\n      "attempt": 1,');
  });

  it('formats raw single-line json object output as json code', () => {
    const { container } = render(<MarkdownContent markdown={'{"ok":true,"nested":{"count":2}}'} />);

    const code = container.querySelector('code.language-json');
    expect(code).toBeInTheDocument();
    expect(code?.textContent).toContain('\n  "ok": true,');
    expect(code?.textContent).toContain('\n  "nested": {');
  });

  it('does not format json-looking text embedded in prose', () => {
    render(<MarkdownContent markdown={'Result: {"ok":true}'} />);

    expect(screen.getByText('Result: {"ok":true}')).toBeInTheDocument();
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
