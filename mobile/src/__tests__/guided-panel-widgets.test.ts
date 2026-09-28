import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createElement } from 'react';
import { Text } from 'react-native';
import TestRenderer, { act, type ReactTestRenderer } from 'react-test-renderer';
import type { StylePanel } from '@/api/types';
import { GuidedPanel, StateWidget } from '@/components/guided-panel';
import { normalizeStylePanel } from '@/lib/styles';

/**
 * §1.16.4 at render level: the same golden `withState` panel the Mac's
 * StyleWidgetPresentationTests reads, drawn by the real component.
 */
function goldenWithState(): StylePanel {
  const golden = join(__dirname, '..', '..', '..', 'styles', 'golden', 'superpowers.panel.json');
  const recorded = JSON.parse(readFileSync(golden, 'utf8')) as Record<string, unknown>;
  return normalizeStylePanel(recorded.withState)!;
}

function render(panel: StylePanel): ReactTestRenderer {
  let renderer: ReactTestRenderer | undefined;
  act(() => {
    renderer = TestRenderer.create(
      createElement(GuidedPanel, {
        panel,
        hasText: false,
        disabled: false,
        running: false,
        onSelectGroup: () => undefined,
        onRun: () => undefined,
      }),
    );
  });
  return renderer!;
}

describe('guided panel state widgets (§1.16.4)', () => {
  it('draws the label on one line with an ellipsis, as the Mac does', () => {
    const renderer = render(goldenWithState());
    const label = renderer.root.findAll(
      (node) => node.type === Text && node.props.children === '서브에이전트 2회 시작',
    );
    expect(label.map((node) => [node.props.numberOfLines, node.props.ellipsizeMode])).toEqual([[1, 'tail']]);
    expect(renderer.root.findAllByType(StateWidget)).toHaveLength(2);
    act(() => renderer.unmount());
  });

  it('draws no widget block when every widget is empty, as the Mac leaves it out', () => {
    const panel: StylePanel = {
      ...goldenWithState(),
      widgets: [
        { kind: 'list', items: [] },
        { kind: 'label', text: '' },
      ],
    };
    const renderer = render(panel);
    expect(renderer.root.findAllByType(StateWidget)).toHaveLength(0);
    act(() => renderer.unmount());
  });
});
