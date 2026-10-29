// The result head in 기록 and 파이프라인 reads the host's title (Core MightyGraphBlockModel.ResultTitle), 최종 결과 without one.
import { describe, expect, it } from 'vitest';
import ko from '@locales/ko.json';
import { translator } from '../i18n/locale';
import { structureResultLook, timelineResultTitle } from './resultTitle';

const t = translator([ko as Record<string, string>]);
const soFar = t('graph.block.resultSoFar', { count: 2 });

describe('result titles', () => {
  it('read the host title of a 기록 group, 최종 결과 without one', () => {
    expect(soFar).toBe('현재까지의 결과 · 백그라운드 작업 2개 기다리는 중');
    expect(timelineResultTitle({ resultTitle: soFar }, t)).toBe(soFar);
    expect(timelineResultTitle({}, t)).toBe('최종 결과');
  });

  it('read the host title of a pipeline card, 최종 결과 without one, and keep the failed and stopped titles', () => {
    expect(structureResultLook({ status: 'completed', resultTitle: soFar }, t)).toEqual({ title: soFar, icon: 'badge-check' });
    expect(structureResultLook({ status: 'completed' }, t)).toEqual({ title: '최종 결과', icon: 'badge-check' });
    expect(structureResultLook({ status: 'error', resultTitle: soFar }, t)).toEqual({ title: t('graph.block.resultError'), icon: 'triangle-alert' });
    expect(structureResultLook({ status: 'stopped' }, t)).toEqual({ title: t('graph.block.resultStopped'), icon: 'circle-stop' });
  });

  it('draw the result so far neutral: a clock and no outcome ink', () => {
    expect(structureResultLook({ status: 'completed', resultTitle: soFar, resultWaiting: true }, t)).toEqual({ title: soFar, icon: 'clock', soFar: true });
    expect(structureResultLook({ status: 'error', resultTitle: soFar, resultWaiting: true }, t)).toEqual({ title: t('graph.block.resultError'), icon: 'triangle-alert' });
  });
});
