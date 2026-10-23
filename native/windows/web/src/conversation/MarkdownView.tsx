import { memo, type ReactNode } from 'react';
import { useServices } from '../app/context';
import { markdown, type Block, type Inline } from './markdown';
import type { Question } from './questionnaire';

/**
 * A code block (AgentTranscript.Code): its language over the code in mono on the code surface. `data-code-block` is what
 * "copy code block" finds under the pointer; the text inside is the block's whole code.
 */
export function CodeBlock({ text, language }: { text: string; language?: string | null }) {
  const name = language?.split(' ').find((word) => word.length > 0);
  return (
    <div className="code-block" data-code-block="">
      {name && <div className="code-language">{name}</div>}
      <pre data-text="" data-code="">{text.replace(/[\r\n]+$/, '')}</pre>
    </div>
  );
}

/** The questions an agent asks, read only (AgentTranscript.Questionnaire): header, question, ○/☐ options and their notes. */
export function Questionnaire({ questions }: { questions: readonly Question[] }) {
  const { t } = useServices();
  return (
    <div className="questionnaire" data-questionnaire="">
      {questions.map((question, index) => (
        <section key={question.question} className="questionnaire-question">
          <div className="questionnaire-header" data-text="">
            {`${index + 1}. ${question.header}  ·  ${t(question.multiSelect ? 'phone.questionnaire.multiple' : 'phone.questionnaire.single')}`}
          </div>
          <div className="questionnaire-title" data-text="">{question.question}</div>
          <ul className={'questionnaire-options' + (question.multiSelect ? ' multiple' : '')}>
            {question.options.map((option) => (
              <li key={option.label}>
                <div className="questionnaire-label" data-text="">{option.label}</div>
                {option.description.length > 0 && <div className="questionnaire-description" data-text="">{option.description}</div>}
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}

function Spans({ nodes }: { nodes: readonly Inline[] }): ReactNode {
  const { t } = useServices();
  return nodes.map((node, index) => {
    switch (node.type) {
      case 'text':
        return node.text;
      case 'code':
        return <code key={index} className="md-code">{node.text}</code>;
      case 'strong':
        return <strong key={index}><Spans nodes={node.children} /></strong>;
      case 'em':
        return <em key={index}><Spans nodes={node.children} /></em>;
      case 'strongEm':
        return <strong key={index}><em><Spans nodes={node.children} /></em></strong>;
      case 'strike':
        return <s key={index}><Spans nodes={node.children} /></s>;
      case 'autolink':
        return <span key={index} className="md-link" data-url={node.url} role="link" tabIndex={0}>{node.url}</span>;
      case 'link':
        // The page opens nothing by itself: a web address is shown after its label, as the WinUI transcript does.
        return node.kind === 'web' ? (
          <span key={index}>
            <span className="md-link" data-url={node.target} role="link" tabIndex={0}><Spans nodes={node.children} /></span>
            {' (' + node.target + ')'}
          </span>
        ) : (
          <span key={index} className="md-local" data-path={node.target} role="link" tabIndex={0}>
            <Spans nodes={node.children} />
            {' (' + node.target + ')'}
          </span>
        );
      case 'image':
        return <Picture key={index} node={node} note={(key) => t(key)} />;
    }
  });
}

/** A Markdown picture: a data image drawn; a remote one never fetched; a file named for the reference preview; else refused. */
function Picture({ node, note }: { node: Extract<Inline, { type: 'image' }>; note: (key: string) => string }) {
  switch (node.kind) {
    case 'data':
      return <img className="md-image" src={node.source} alt={node.alt} />;
    case 'remote':
      return (
        <span>
          <span className="md-link" data-url={node.source} role="link" tabIndex={0}>{node.alt || node.source}</span>
          {' (' + node.source + ')'}
          <span className="md-note">{' (' + note('images.remote') + ')'}</span>
        </span>
      );
    case 'local':
      return (
        <span>
          {node.alt && <span className="md-quiet">{node.alt + ' '}</span>}
          <span className="md-local" data-path={node.source} role="link" tabIndex={0}>{'(' + node.source + ')'}</span>
        </span>
      );
    case 'refused':
      return (
        <span>
          {node.alt || node.source.slice(0, 120)}
          <span className="md-note">{' (' + note('images.refused') + ')'}</span>
        </span>
      );
    case 'alt':
      return <span className="md-quiet">{node.alt}</span>;
  }
}

function Blocks({ blocks }: { blocks: readonly Block[] }): ReactNode {
  return blocks.map((block, index) => {
    switch (block.type) {
      case 'paragraph':
        return <p key={index} className="md-paragraph" data-text=""><Spans nodes={block.inline} /></p>;
      case 'heading':
        return <div key={index} className={'md-heading md-h' + Math.min(4, block.level)} role="heading" aria-level={block.level} data-text=""><Spans nodes={block.inline} /></div>;
      case 'code':
        return <CodeBlock key={index} text={block.text} language={block.language} />;
      case 'questionnaire':
        return <Questionnaire key={index} questions={block.questions} />;
      case 'quote':
        return <blockquote key={index} className="md-quote"><Blocks blocks={block.children} /></blockquote>;
      case 'list': {
        const List = block.ordered ? 'ol' : 'ul';
        return (
          <List key={index} className="md-list">
            {block.items.map((item, at) => (
              <li
                key={at}
                className={'md-item' + (item.task ? ' md-task ' + item.task : '')}
                data-marker={item.task ? undefined : block.ordered ? item.ordinal + '.' : undefined}
              >
                <Blocks blocks={item.children} />
              </li>
            ))}
          </List>
        );
      }
      case 'rule':
        return <div key={index} className="md-rule" role="separator" />;
      case 'table':
        return (
          <div key={index} className="md-table-scroll">
            <table className="md-table" data-md="table">
              <thead>
                <tr>{block.rows[0]!.map((cell, column) => <th key={column} style={{ textAlign: block.align[column] }} data-text=""><Spans nodes={cell} /></th>)}</tr>
              </thead>
              <tbody>
                {block.rows.slice(1).map((row, at) => (
                  <tr key={at}>{row.map((cell, column) => <td key={column} style={{ textAlign: block.align[column] }} data-text=""><Spans nodes={cell} /></td>)}</tr>
                ))}
              </tbody>
            </table>
          </div>
        );
      case 'source':
        return <div key={index} className="md-source">{block.lines.map((line, at) => <p key={at} className="md-paragraph" data-text="">{line}</p>)}</div>;
    }
  });
}

/** An entry's Markdown, parsed once per text (markdown()'s cache) and drawn again only when the text changes. */
export const Markdown = memo(function Markdown({ text }: { text: string }) {
  return <div className="markdown"><Blocks blocks={markdown(text)} /></div>;
});
