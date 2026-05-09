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

export default function MarkdownContent({ markdown, compact = false, className }: Props): React.JSX.Element {
  const classes = [
    'markdown-content',
    compact ? 'markdown-content--compact' : null,
    className,
  ]
    .filter(Boolean)
    .join(' ');

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
        }}
      >
        {markdown}
      </ReactMarkdown>
    </div>
  );
}
