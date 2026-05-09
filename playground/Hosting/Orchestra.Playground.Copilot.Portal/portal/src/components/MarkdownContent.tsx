import React from 'react';
import ReactMarkdown from 'react-markdown';
import rehypeSanitize from 'rehype-sanitize';
import remarkBreaks from 'remark-breaks';
import remarkGfm from 'remark-gfm';

interface Props {
  markdown: string;
  compact?: boolean;
  className?: string;
}

function formatJson(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed || (!trimmed.startsWith('{') && !trimmed.startsWith('['))) {
    return null;
  }

  try {
    return JSON.stringify(JSON.parse(trimmed), null, 2);
  } catch {
    return null;
  }
}

function normalizeMarkdown(markdown: string): string {
  const formattedJson = formatJson(markdown);
  if (formattedJson) {
    return `\`\`\`json\n${formattedJson}\n\`\`\``;
  }

  return markdown.replace(
    /(```|~~~)[ \t]*json[^\r\n]*\r?\n([\s\S]*?)\r?\n?\1/gim,
    (match, fence: string, content: string) => {
      const formattedBlock = formatJson(content);
      return formattedBlock ? `${fence}json\n${formattedBlock}\n${fence}` : match;
    },
  );
}

function getTextContent(node: React.ReactNode): string {
  if (typeof node === 'string' || typeof node === 'number') {
    return String(node);
  }
  if (Array.isArray(node)) {
    return node.map(getTextContent).join('');
  }
  return '';
}

export default function MarkdownContent({ markdown, compact = false, className }: Props): React.JSX.Element {
  const classes = [
    'markdown-content',
    compact ? 'markdown-content--compact' : null,
    className,
  ]
    .filter(Boolean)
    .join(' ');
  const displayMarkdown = normalizeMarkdown(markdown);

  return (
    <div className={classes}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm, remarkBreaks]}
        rehypePlugins={[rehypeSanitize]}
        skipHtml
        components={{
          a({ href, children, ...props }) {
            return (
              <a href={href} target="_blank" rel="noreferrer noopener" {...props}>
                {children}
              </a>
            );
          },
          code({ className, children, ...props }) {
            const language = /language-(\w+)/.exec(className || '')?.[1]?.toLowerCase();
            const content = getTextContent(children);
            const formattedJson = language === 'json' ? formatJson(content) : null;

            return (
              <code className={className} {...props}>
                {formattedJson ?? children}
              </code>
            );
          },
        }}
      >
        {displayMarkdown}
      </ReactMarkdown>
    </div>
  );
}
