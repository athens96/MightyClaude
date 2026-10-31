// The result head in 기록 and 파이프라인 reads the host's title (Core MightyGraphBlockModel.ResultTitle), 최종 결과 without one.
import { describe, expect, it } from 'vitest';
import ko from '@locales/ko.json';
import { translator } from '../i18n/locale';
import { structureResultLook, timelineResultTitle } from './resultTitle';

const t = translator([ko as Record<string, string>]);
const awaiting = t('graph.block.awaitingNotice');

describe('result titles', () => {
  it('read the host title of a 기록 group, 최종 결과 without one', () => {
    expect(awaiting).toBe('알림 대기 중');
    expect(timelineResultTitle({ resultTitle: awaiting }, t)).toBe(awaiting);
    expect(timelineResultTitle({}, t)).toBe('최종 결과');
  });

  it('read the host title of a pipeline card, 최종 결과 without one, and keep the failed and stopped titles', () => {
    expect(structureResultLook({ status: 'completed', resultTitle: awaiting }, t)).toEqual({ title: awaiting, icon: 'badge-check' });
    expect(structureResultLook({ status: 'completed' }, t)).toEqual({ title: '최종 결과', icon: 'badge-check' });
    expect(structureResultLook({ status: 'error', resultTitle: awaiting }, t)).toEqual({ title: t('graph.block.resultError'), icon: 'triangle-alert' });
    expect(structureResultLook({ status: 'stopped' }, t)).toEqual({ title: t('graph.block.resultStopped'), icon: 'circle-stop' });
  });

  it('draw a card awaiting notices with an hourglass in the wait ink', () => {
    expect(structureResultLook({ status: 'completed', resultTitle: awaiting, resultAwaiting: true }, t)).toEqual({ title: awaiting, icon: 'hourglass', awaiting: true });
    expect(structureResultLook({ status: 'completed', resultAwaiting: true }, t)).toEqual({ title: awaiting, icon: 'hourglass', awaiting: true });
    expect(structureResultLook({ status: 'error', resultTitle: awaiting, resultAwaiting: true }, t)).toEqual({ title: t('graph.block.resultError'), icon: 'triangle-alert' });
  });
});
