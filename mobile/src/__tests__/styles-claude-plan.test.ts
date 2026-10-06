import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createElement } from 'react';
import { Text } from 'react-native';
import TestRenderer, { act } from 'react-test-renderer';
import type { StyleTaskItem } from '@/api/types';
import { TaskListWidget } from '@/components/guided-panel';
import { resetLanguage } from '@/lib/i18n';
import {
  MAX_TASK_LIST_ITEMS,
  elapsedText,
  guidedRequestFor,
  normalizeStylePanel,
  parseTimestamp,
  progressBarDisplay,
  styleViewModel,
  taskRowDisplay,
} from '@/lib/styles';

/**
 * The bundled "클러드 플랜" style as the phone's generic renderer reads it (docs/mighty-styles.md
 * §1.17): the same golden the Mac records and Windows Core checks, the plan stage moving the
 * phase, the checklist bar with its current step, and the background task list.
 *
 * Read with `fs`, not `import`: mobile/tsconfig.json has no `resolveJsonModule`.
 */
const GOLDEN = join(__dirname, '..', '..', '..', 'styles', 'golden', 'claude-plan.panel.json');
const recorded = JSON.parse(readFileSync(GOLDEN, 'utf8')) as Record<string, unknown>;

beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

describe('the claude-plan golden', () => {
  it('moves the phase with the plan stage: plan, approve, execute', () => {
    const stages = ['planStagePlanning', 'planStageAwaitingApproval', 'planStageExecuting'];
    const phases = stages.map((key) => normalizeStylePanel(recorded[key])?.phase?.id);
    expect(phases).toEqual(['plan', 'approve', 'execute']);
    const approve = normalizeStylePanel(recorded.planStageAwaitingApproval)!;
    // A plan waiting for its answer is a turn in flight: no chips, its own guidance.
    expect(styleViewModel(approve).actions).toEqual([]);
    expect(approve.guidance).toBe('계획을 검토하고 승인하세요 · 수정 요청이나 취소도 할 수 있습니다');
    expect(approve.presentation.headerTitle).toBe('클러드 플랜 · 승인');
  });

  it('offers a new plan, then a new plan and verify after execution', () => {
    const planning = styleViewModel(normalizeStylePanel(recorded.empty)!);
    expect(planning.actions.map((view) => view.action.id)).toEqual(['new-plan']);
    const executing = normalizeStylePanel(recorded.withState)!;
    expect(styleViewModel(executing).actions.map((view) => view.action.id)).toEqual(['new-plan', 'verify']);
    expect(guidedRequestFor(executing, 'verify', '무시됨').text).toBeUndefined();
    expect(guidedRequestFor(executing, 'new-plan', '결제 모듈').text).toBe('결제 모듈');
  });

  it('draws the checklist bar, its current step, the background line and the task list', () => {
    const panel = normalizeStylePanel(recorded.withState)!;
    const widgets = styleViewModel(panel).widgets;
    expect(widgets.map((widget) => widget.kind)).toEqual(['progressBar', 'label', 'label', 'taskList']);
    const bar = widgets[0];
    expect(bar?.kind === 'progressBar' ? progressBarDisplay(bar) : undefined).toEqual({ fraction: 3 / 7, text: '3/7' });
    expect(widgets[1]).toEqual({ kind: 'label', text: '진행 중: doing step 4' });
    expect(widgets[2]).toEqual({ kind: 'label', text: '턴 완료 · 백그라운드 1개 실행 중' });
    expect(widgets[3]).toEqual({
      kind: 'taskList',
      items: [
        { text: 'review the diff', kind: 'agent', status: 'running', startedAt: '2026-10-06T01:00:00.000Z' },
        {
          text: 'npm test',
          kind: 'shell',
          status: 'completed',
          startedAt: '2026-10-06T01:00:05.000Z',
          endedAt: '2026-10-06T01:01:10.000Z',
        },
      ],
    });
  });

  it('keeps a new plan from showing the last one: the bar empty, the empty lines left out', () => {
    const panel = normalizeStylePanel(recorded.planStagePlanning)!;
    const widgets = styleViewModel(panel).widgets;
    expect(widgets[0]).toEqual({ kind: 'progressBar', value: 0, total: 0 });
    expect(widgets.some((widget) => widget.kind === 'label' && widget.text.startsWith('진행 중'))).toBe(false);
  });
});

describe('task rows', () => {
  const start = Date.parse('2026-10-06T01:00:00.000Z');

  it('say kind, status and the elapsed time, to now while running and to the end once ended', () => {
    const running: StyleTaskItem = { text: 'review', kind: 'agent', status: 'running', startedAt: '2026-10-06T01:00:00.000Z' };
    expect(taskRowDisplay(running, start + 45_000)).toEqual({ text: 'review', detail: '에이전트 · 실행 중 · 45초', running: true });
    expect(taskRowDisplay(running, start + 192_000).detail).toBe('에이전트 · 실행 중 · 3분 12초');
    const ended: StyleTaskItem = { ...running, kind: 'shell', status: 'completed', endedAt: '2026-10-06T01:01:05.000Z' };
    expect(taskRowDisplay(ended, start + 5 * 3_600_000).detail).toBe('셸 · 완료 · 1분 5초');
    expect(taskRowDisplay({ ...running, text: '', kind: 'other', status: 'unknown' }, start).text).toBe('작업');
  });

  it("reads a Windows host's seven-digit round-trip stamps", () => {
    expect(parseTimestamp('2026-10-06T01:00:00.1234567+00:00')).toBe(Date.parse('2026-10-06T01:00:00.123Z'));
    expect(parseTimestamp('2026-10-06T01:00:00Z')).toBe(start);
    const windows: StyleTaskItem = {
      text: 'review',
      kind: 'agent',
      status: 'completed',
      startedAt: '2026-10-06T10:00:00.0000000+09:00',
      endedAt: '2026-10-06T10:00:45.9999999+09:00',
    };
    expect(taskRowDisplay(windows, start).detail).toBe('에이전트 · 완료 · 45초');
    const payload = JSON.parse(JSON.stringify(recorded.withState)) as Record<string, unknown>;
    payload.widgets = [{ kind: 'taskList', items: [windows] }];
    const widget = normalizeStylePanel(payload)!.widgets?.[0];
    expect(widget?.kind === 'taskList' ? widget.items : []).toEqual([windows]);
  });

  it('formats hours, and reads a clock that went back as 0', () => {
    expect(elapsedText(start, start + 3_725_000)).toBe('1시간 2분');
    expect(elapsedText(start, start - 5_000)).toBe('0초');
  });

  it('are parsed defensively: closed kinds and statuses, a start time required, eight at most', () => {
    const payload = JSON.parse(JSON.stringify(recorded.withState)) as Record<string, unknown>;
    payload.widgets = [
      {
        kind: 'taskList',
        items: [
          { text: 'odd', kind: 'robot', status: 'exploded', startedAt: '2026-10-06T01:00:00.000Z', endedAt: 'never' },
          { text: 'no start', kind: 'agent', status: 'running' },
          ...Array.from({ length: 12 }, (_, index) => ({
            text: `task ${index}`,
            kind: 'agent',
            status: 'running',
            startedAt: '2026-10-06T01:00:00.000Z',
          })),
        ],
      },
    ];
    const widget = normalizeStylePanel(payload)!.widgets?.[0];
    expect(widget?.kind).toBe('taskList');
    const items = widget?.kind === 'taskList' ? widget.items : [];
    expect(items).toHaveLength(MAX_TASK_LIST_ITEMS);
    expect(items[0]).toEqual({ text: 'odd', kind: 'other', status: 'unknown', startedAt: '2026-10-06T01:00:00.000Z' });
    expect(items.some((item) => item.text === 'no start')).toBe(false);
    // An empty task list keeps its place in the payload but draws nothing.
    payload.widgets = [{ kind: 'taskList', items: [] }];
    expect(styleViewModel(normalizeStylePanel(payload)!).widgets).toEqual([]);
  });

  it('render one row each, the elapsed time ticking while a task runs', () => {
    jest.useFakeTimers();
    jest.setSystemTime(start + 10_000);
    try {
      const items: StyleTaskItem[] = [
        { text: 'review', kind: 'agent', status: 'running', startedAt: '2026-10-06T01:00:00.000Z' },
        { text: 'build', kind: 'shell', status: 'failed', startedAt: '2026-10-06T01:00:00.000Z', endedAt: '2026-10-06T01:00:03.000Z' },
      ];
      let renderer!: TestRenderer.ReactTestRenderer;
      act(() => {
        renderer = TestRenderer.create(createElement(TaskListWidget, { items, tint: '#3366ff' }));
      });
      const texts = () => renderer.root.findAllByType(Text).map((node) => node.props.children as string);
      expect(texts()).toEqual(['review', '에이전트 · 실행 중 · 10초', 'build', '셸 · 실패 · 3초']);
      act(() => {
        jest.advanceTimersByTime(2_000);
      });
      expect(texts()[1]).toBe('에이전트 · 실행 중 · 12초');
      act(() => renderer.unmount());
    } finally {
      jest.useRealTimers();
    }
  });
});
