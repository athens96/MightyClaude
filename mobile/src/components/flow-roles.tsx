import { useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import type { MobileAdvisor, MobileBlock, MobileBlockCard, MobileDelegateLine, MobileFlowLog, MobileProgress, MobileProgressRow, MobileReturn } from '@/api/types';
import { useNow } from '@/hooks/use-now';
import {
  advisorShown,
  advisorWhen,
  cardFigures,
  inkColor,
  logClock,
  progressTime,
  statusText,
  stillRunning,
} from '@/lib/flow-roles';
import { toneOf } from '@/lib/status-tone';
import { t } from '@/lib/i18n';
import { monoText, radius, spacing, toneColors, useStyles, usePalette, type Palette } from '@/theme';

/**
 * The role view's pieces on the phone (docs/mobile-remote.md "역할 보기"): the role's ink down a block's edge, a compact row's lines,
 * the delegate line over the rows a block handed work to, the way back where the result will come, the advisor's summary under a
 * request and the session log's status line over the flow. The words are the host's; the phone keeps only the inks (mapped onto its
 * palette, `inkColor`) and a running row's time, read against its own clock once a second.
 */

/** The block's role ink as a bar down its card's leading edge (the desktops' header strip). */
export function RoleBar({ ink }: { ink: string }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return <View pointerEvents="none" style={[styles.bar, { backgroundColor: inkColor(palette, ink) }]} />;
}

/**
 * A compact row's lines under its title (`FlowCard`): who it is (type · model, or a background command's command), the one line of
 * what it does now (or answered, or last wrote), and its state and figures with its time, which keeps moving while it runs.
 */
export function CardLines({ block, card, identity, task }: { block: MobileBlock; card: MobileBlockCard; identity: string; task: boolean }) {
  const styles = useStyles(makeStyles);
  const running = stillRunning(block.status);
  const now = useNow(1000, running && card.startedAt !== undefined);
  return (
    <>
      {card.step ? (
        <Text
          numberOfLines={1}
          style={styles.cardStep}
          accessibilityLabel={t('graph.card.inStep', { step: card.step })}
          testID={`mighty-card-step-${block.id}`}
        >
          {`↳ ${card.step}`}
        </Text>
      ) : null}
      {identity ? (
        <Text numberOfLines={1} style={[styles.cardIdentity, task && styles.mono]} testID={`mighty-card-identity-${block.id}`}>
          {identity}
        </Text>
      ) : null}
      {card.activity ? (
        <Text numberOfLines={1} style={[styles.cardActivity, task && styles.mono]} testID={`mighty-card-activity-${block.id}`}>
          {card.activity}
        </Text>
      ) : null}
      <Text style={styles.cardFigures} testID={`mighty-card-figures-${block.id}`}>
        {cardFigures(card, block.status, now)}
      </Text>
    </>
  );
}

/** The rail through a row that is not a block, so the timeline stays one line. */
function Rail({ color }: { color: string | undefined }) {
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.railColumn}>
      {color ? <View style={[styles.rail, { backgroundColor: color }]} /> : null}
    </View>
  );
}

/**
 * The line over the rows a block delegated to (`FlowDelegation`): "하위 에이전트 3개에 위임 · Haiku 5.5 · 2/3 실행 중", or for a row of both
 * "3개 병렬 · 하위 에이전트 1 · 작업 2 · …"; the counts in the text ink, each part in its role's, the model in the sub-agent ink and the
 * progress in the running blue.
 */
export function DelegateRow({ line, rail }: { line: MobileDelegateLine; rail: string | undefined }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.lineRow} accessible accessibilityLabel={line.text} testID="mighty-delegate">
      <Rail color={rail} />
      <Text style={styles.delegate}>
        <Text style={styles.branch}>{'↳ '}</Text>
        <Text style={{ color: palette.text }}>{line.head}</Text>
        {(line.parts ?? []).map((part, index) => (
          <Text key={index} style={{ color: inkColor(palette, part.ink) }}>{` · ${part.text}`}</Text>
        ))}
        {line.model ? <Text style={{ color: palette.blockAgent }}>{` · ${line.model}`}</Text> : null}
        {line.progress ? <Text style={{ color: palette.accent }}>{` · ${line.progress}`}</Text> : null}
      </Text>
    </View>
  );
}

/**
 * The way back where the request's result will come (`FlowReturn`), while it is not there: "병렬 2/3 실행 중 · 보고를 기다리는 중", then
 * "메인으로 복귀 · 검토 중". In the lead's ink, ringed in the running blue while something it handed work to still runs.
 */
export function ReturnRow({ returning, rail }: { returning: MobileReturn; rail: string | undefined }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.lineRow} testID="mighty-return">
      <View style={styles.railColumn}>
        {rail ? <View style={[styles.railAbove, { backgroundColor: rail }]} /> : null}
        <View style={[styles.returnNode, { borderColor: palette.blockTask, backgroundColor: palette.surfaceRaised }]} />
      </View>
      <View style={[styles.returnBox, { borderColor: returning.waiting ? toneColors(palette, 'run').fill : palette.border }]}>
        <Text style={[styles.returnText, { color: palette.blockTask }]}>{`⤴ ${returning.text}`}</Text>
      </View>
    </View>
  );
}

/**
 * The advisor of a request (`FlowAdvisor`): "조언자 · Opus 5.5 · 대기 중", a line per consultation (who asked, when and how long the reply
 * took, where in the work and how it ended; the newest few, the rest counted), the note that the advice went to Claude encrypted, and
 * the totals. In the advisor's pink; ringed in the running blue while it reviews.
 */
export function AdvisorCard({ advisor }: { advisor: MobileAdvisor }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const { rows, earlier } = advisorShown(advisor);
  const consulting = advisor.state === 'consulting';
  return (
    <View style={[styles.advisor, { borderColor: consulting ? toneColors(palette, 'run').fill : palette.border }]} testID="mighty-advisor">
      <View pointerEvents="none" style={[styles.bar, { backgroundColor: palette.blockSteer }]} />
      <Text style={[styles.advisorHeader, { color: palette.blockSteer }]}>{advisor.header}</Text>
      {earlier > 0 ? <Text style={styles.advisorFaint}>{t('graph.advisor.earlier', { count: earlier })}</Text> : null}
      {rows.length === 0 && advisor.empty ? <Text style={styles.advisorFaint}>{advisor.empty}</Text> : null}
      {rows.map((row) => {
        const when = advisorWhen(row);
        return (
          <View key={row.id} style={styles.advisorRow}>
            <Text numberOfLines={1} style={styles.advisorWho}>
              <Text style={{ color: inkColor(palette, row.ink), fontWeight: '700' }}>{row.caller}</Text>
              {when ? <Text style={{ color: palette.textFaint }}>{` · ${when}`}</Text> : null}
            </Text>
            <Text numberOfLines={2} style={styles.advisorLine}>{row.line}</Text>
          </View>
        );
      })}
      {advisor.note ? <Text style={styles.advisorFaint}>{advisor.note}</Text> : null}
      {advisor.totals ? <Text style={styles.advisorTotals}>{advisor.totals}</Text> : null}
    </View>
  );
}

/**
 * The session log's status line over the flow (`FlowLog`): "Sonnet 5.5 실행 중 · 작업 2개 실행 중 · 조언자 대기", each part in its role's ink.
 * It folds the newest activity's last rows under it (time on the phone's clock, who in their ink, what); folded at first, as the
 * screen is small.
 */
export function FlowLogStrip({ log }: { log: MobileFlowLog }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [open, setOpen] = useState(false);
  const opens = log.rows.length > 0;
  const status = (
    <Text numberOfLines={2} style={styles.status} testID="mighty-log-status">
      {log.status.map((part, index) => (
        <Text key={index} style={{ color: inkColor(palette, part.ink) }}>{`${index > 0 ? ' · ' : ''}${part.text}`}</Text>
      ))}
    </Text>
  );
  return (
    <View style={styles.strip} testID="mighty-log">
      {opens ? (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={statusText(log)}
          accessibilityHint={t(open ? 'graph.log.hide' : 'graph.log.show')}
          accessibilityState={{ expanded: open }}
          onPress={() => setOpen((value) => !value)}
          style={({ pressed }) => [styles.stripHead, pressed && styles.pressed]}
        >
          <Text style={styles.chevron}>{open ? '▾' : '▸'}</Text>
          {status}
        </Pressable>
      ) : (
        <View style={styles.stripHead} accessible accessibilityLabel={statusText(log)}>
          {status}
        </View>
      )}
      {open
        ? log.rows.map((row) => (
            <View key={row.id} style={styles.logRow} accessible accessibilityLabel={`${logClock(row.at)} ${row.who} ${row.what}`}>
              <Text style={styles.logClock}>{logClock(row.at)}</Text>
              <Text numberOfLines={1} style={styles.logLine}>
                <Text style={{ color: inkColor(palette, row.ink), fontWeight: '700' }}>{row.who}</Text>
                <Text style={{ color: palette.textMuted }}>{`  ${row.what}`}</Text>
              </Text>
            </View>
          ))
        : null}
    </View>
  );
}

/** The timeline's node column (`mighty-blocks.tsx`: its 25 pt node and 9 pt of room), so a rail drawn here lines up with it. */
const RAIL_COLUMN = 34;
/** The small node the way back stands on. */
const RETURN_NODE = 11;

/**
 * The progress panel pinned over a session's Mighty view (the desktops' `MightyGraphProgressPanel`): the request it is about ("요청 27", its
 * state and first line; a tap goes to it), then its plan, a line a step with its mark and its time (the step in progress keeps time on the
 * phone's clock), and the line that counts the steps left out. Folded, the header and the request line stay.
 */
export function ProgressPanel({ progress, onRequest }: { progress: MobileProgress; onRequest?: () => void }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [folded, setFolded] = useState(false);
  const ticking = progress.running && progress.rows.some((row) => row.state === 'current' && row.startedAt !== undefined && row.endedAt === undefined);
  const now = useNow(1000, ticking);
  const tone = toneColors(palette, toneOf(progress.status));
  return (
    <View style={styles.progress} testID="mighty-progress">
      <View style={styles.progressHead}>
        <Text style={styles.progressTitle}>{progress.title}</Text>
        {progress.count ? <Text style={styles.progressCount}>{progress.count}</Text> : null}
        <View style={styles.flex} />
        {progress.rows.length > 0 ? (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t(folded ? 'graph.steps.expand' : 'graph.steps.collapse')}
            hitSlop={8}
            onPress={() => setFolded((value) => !value)}
            testID="mighty-progress-fold"
          >
            <Text style={styles.progressFold}>{folded ? '⌄' : '⌃'}</Text>
          </Pressable>
        ) : null}
      </View>
      <Pressable accessibilityRole="button" onPress={onRequest} disabled={!onRequest} testID="mighty-progress-request">
        <View style={styles.progressRequestRow}>
          <Text style={styles.progressHeading}>{progress.heading}</Text>
          <View style={[styles.progressPill, { backgroundColor: tone.soft }]}>
            <Text style={[styles.progressPillText, { color: tone.ink }]}>{progress.stateWord}</Text>
          </View>
        </View>
        {progress.request ? (
          <Text numberOfLines={2} style={styles.progressRequest}>
            {progress.request}
          </Text>
        ) : null}
      </Pressable>
      {!folded && progress.rows.length > 0 ? (
        <View style={styles.progressRows}>
          {progress.rows.map((row) => (
            <ProgressLine key={row.index} row={row} time={progressTime(row, progress.running, now)} />
          ))}
          {progress.more ? <Text style={styles.progressMore}>{progress.more}</Text> : null}
        </View>
      ) : null}
    </View>
  );
}

function ProgressLine({ row, time }: { row: MobileProgressRow; time: string | undefined }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const mark = { done: '✓', current: '●', pending: '○', stopped: '■', failed: '!' }[row.state];
  const color = {
    done: toneColors(palette, 'done').fill,
    current: toneColors(palette, 'run').fill,
    pending: palette.textFaint,
    stopped: toneColors(palette, 'stop').fill,
    failed: toneColors(palette, 'err').fill,
  }[row.state];
  return (
    <View
      style={[styles.progressRow, row.state === 'current' && { backgroundColor: toneColors(palette, 'run').soft }]}
      testID={`mighty-progress-row-${row.index}`}
    >
      <Text style={[styles.progressMark, { color }]}>{mark}</Text>
      <Text
        numberOfLines={1}
        style={[styles.progressText, row.state === 'pending' && styles.progressPending, row.state === 'current' && styles.progressCurrent]}
      >
        {row.text}
      </Text>
      {time ? <Text style={styles.progressTime}>{time}</Text> : null}
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    flex: { flex: 1 },
    progress: {
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.lg,
      gap: spacing.xs,
      marginHorizontal: spacing.lg,
      marginTop: spacing.sm,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
    },
    progressHead: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    progressTitle: { color: palette.text, fontSize: 13, fontWeight: '800' },
    progressCount: { color: palette.textMuted, fontSize: 12, fontWeight: '600' },
    progressFold: { color: palette.textMuted, fontSize: 14, fontWeight: '700' },
    progressRequestRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    progressHeading: { color: palette.text, fontSize: 12.5, fontWeight: '700' },
    progressPill: { borderRadius: radius.md, paddingHorizontal: spacing.sm, paddingVertical: 1 },
    progressPillText: { fontSize: 11, fontWeight: '700' },
    progressRequest: { color: palette.textMuted, fontSize: 12.5, lineHeight: 17 },
    progressRows: { gap: spacing.xs, paddingTop: spacing.xs },
    progressRow: { alignItems: 'center', borderRadius: radius.md, flexDirection: 'row', gap: spacing.sm, minHeight: 24, paddingHorizontal: spacing.xs },
    progressMark: { fontSize: 12, fontWeight: '800', textAlign: 'center', width: 14 },
    progressText: { color: palette.text, flex: 1, fontSize: 12.5 },
    progressPending: { color: palette.textFaint },
    progressCurrent: { fontWeight: '700' },
    progressTime: { color: palette.textFaint, fontSize: 11 },
    progressMore: { color: palette.textFaint, fontSize: 11.5, paddingHorizontal: spacing.xs },
    cardStep: { color: palette.blockTask, fontSize: 11.5, fontWeight: '700' },
    bar: { bottom: 0, left: 0, position: 'absolute', top: 0, width: 3 },
    mono: { ...monoText, fontSize: 11.5, lineHeight: 16 },
    cardIdentity: { color: palette.textMuted, fontSize: 12 },
    cardActivity: { color: palette.text, fontSize: 12.5 },
    cardFigures: { color: palette.textFaint, fontSize: 11.5 },
    lineRow: { flexDirection: 'row', gap: spacing.md, paddingBottom: spacing.sm },
    railColumn: { alignItems: 'center', width: RAIL_COLUMN },
    rail: { bottom: -spacing.sm, position: 'absolute', top: 0, width: 3 },
    railAbove: { height: spacing.lg, position: 'absolute', top: 0, width: 3 },
    returnNode: { borderRadius: RETURN_NODE / 2, borderWidth: 2, height: RETURN_NODE, marginTop: spacing.lg, width: RETURN_NODE },
    delegate: { flex: 1, fontSize: 11.5, fontWeight: '600', lineHeight: 16, paddingVertical: spacing.xs },
    branch: { color: palette.textFaint },
    returnBox: { borderRadius: radius.md, borderWidth: 1.5, flex: 1, paddingHorizontal: spacing.lg, paddingVertical: spacing.md },
    returnText: { fontSize: 12, fontWeight: '700' },
    advisor: {
      backgroundColor: palette.surface,
      borderRadius: radius.lg,
      borderWidth: 1.5,
      gap: spacing.xs,
      marginBottom: spacing.sm,
      overflow: 'hidden',
      paddingHorizontal: spacing.xl,
      paddingVertical: spacing.md,
    },
    advisorHeader: { fontSize: 12.5, fontWeight: '800' },
    advisorRow: { gap: spacing.xs, paddingVertical: spacing.xs },
    advisorWho: { fontSize: 12 },
    advisorLine: { color: palette.textMuted, fontSize: 12 },
    advisorFaint: { color: palette.textFaint, fontSize: 11.5 },
    advisorTotals: { color: palette.textMuted, fontSize: 11.5, fontWeight: '600' },
    strip: {
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.lg,
      gap: spacing.xs,
      marginHorizontal: spacing.lg,
      marginTop: spacing.sm,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
    },
    stripHead: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm, minHeight: 44 },
    chevron: { color: palette.textFaint, fontSize: 12 },
    status: { flex: 1, fontSize: 13, fontWeight: '600' },
    logRow: { alignItems: 'baseline', flexDirection: 'row', gap: spacing.md, paddingVertical: spacing.xs },
    logClock: { ...monoText, color: palette.textFaint, fontSize: 11 },
    logLine: { flex: 1, fontSize: 12 },
    pressed: { opacity: 0.6 },
  });
