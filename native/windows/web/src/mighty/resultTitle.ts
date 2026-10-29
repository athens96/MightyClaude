// The result head's title in 기록 and 파이프라인: the host's (Core MightyGraphBlockModel.ResultTitle), which reads 현재까지의 결과 with the
// count while background work the request started still runs; 최종 결과 from a host that sends none.
import type { Translate } from '../i18n/locale';
import type { IconName } from '../icons/lucide';
import type { StructureCard, TimelineGroup } from './types';

/** A 기록 group's result head. */
export function timelineResultTitle(group: Pick<TimelineGroup, 'resultTitle'>, t: Translate): string {
  return group.resultTitle ?? t('graph.block.result');
}

/** A 파이프라인 result or summary card's result title and its symbol: a failed or stopped request says so, any other the host's title. */
export function structureResultLook(card: Pick<StructureCard, 'status' | 'resultTitle'>, t: Translate): { title: string; icon: IconName } {
  if (card.status === 'error') return { title: t('graph.block.resultError'), icon: 'triangle-alert' };
  if (card.status === 'stopped') return { title: t('graph.block.resultStopped'), icon: 'circle-stop' };
  return { title: card.resultTitle ?? t('graph.block.result'), icon: 'badge-check' };
}
