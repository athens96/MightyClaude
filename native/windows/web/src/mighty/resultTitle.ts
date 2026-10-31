// The result head's title in 기록 and 파이프라인: the host's (Core MightyGraphBlockModel.ResultTitle), which reads 알림 대기 중 while the
// request awaits the notices of background work it started; 최종 결과 from a host that sends none.
import type { Translate } from '../i18n/locale';
import type { IconName } from '../icons/lucide';
import type { StructureCard, TimelineGroup } from './types';

/** A 기록 group's result head. */
export function timelineResultTitle(group: Pick<TimelineGroup, 'resultTitle'>, t: Translate): string {
  return group.resultTitle ?? t('graph.block.result');
}

/**
 * A 파이프라인 result or summary card's result title and its symbol: a failed or stopped request says so, any other the host's title.
 * Awaiting notices (`resultAwaiting`), it takes an hourglass in the wait ink, not the done seal (`awaiting`).
 */
export function structureResultLook(card: Pick<StructureCard, 'status' | 'resultTitle' | 'resultAwaiting'>, t: Translate): { title: string; icon: IconName; awaiting?: true } {
  if (card.status === 'error') return { title: t('graph.block.resultError'), icon: 'triangle-alert' };
  if (card.status === 'stopped') return { title: t('graph.block.resultStopped'), icon: 'circle-stop' };
  if (card.resultAwaiting) return { title: card.resultTitle ?? t('graph.block.awaitingNotice'), icon: 'hourglass', awaiting: true };
  return { title: card.resultTitle ?? t('graph.block.result'), icon: 'badge-check' };
}
