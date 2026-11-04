import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import * as Clipboard from 'expo-clipboard';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { describeError, isAbortError } from '@/api/client';
import type { WorkMap, WorkMapTopic, WorkMapVersion } from '@/api/types';
import { BottomSheet, DialogLoading } from '@/components/sheets';
import { Button, Card, EmptyState, ErrorBanner } from '@/components/ui';
import { t } from '@/lib/i18n';
import {
  LANE_ORDER,
  chipDate,
  laneColor,
  laneLabel,
  laneShares,
  statusLabel,
  topicCommits,
  topicRequests,
  unavailableReason,
  versionName,
} from '@/lib/work-map';
import { useHostClient } from '@/store/live';
import { showToast } from '@/store/toast';
import {
  monoFontFamily,
  radius,
  spacing,
  typeScale,
  useStyles,
  usePalette,
  type Palette,
} from '@/theme';

/** How often the map is read again while the host groups a version into topics. */
const SUMMARY_POLL_MS = 2000;

/** The paired computer's 작업 지도 for one workspace, read-only (docs/work-map.md "휴대폰"). */
export default function WorkMapScreen() {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const insets = useSafeAreaInsets();
  const { hostId, workspaceId } = useLocalSearchParams<{ hostId: string; workspaceId: string }>();
  const client = useHostClient(hostId);
  const [map, setMap] = useState<WorkMap | undefined>(undefined);
  const [error, setError] = useState<string | undefined>(undefined);
  const [reload, setReload] = useState(0);
  const [refreshing, setRefreshing] = useState(false);
  const [picked, setPicked] = useState<string | undefined>(undefined);
  const [topicIndex, setTopicIndex] = useState<number | undefined>(undefined);
  const [showCommits, setShowCommits] = useState(false);
  const [asking, setAsking] = useState(false);
  const failed = useRef(false);

  const summarizing = map?.summarizing ?? null;

  useEffect(() => {
    if (!client || !workspaceId) return undefined;
    const controller = new AbortController();
    failed.current = false;
    client
      .workMap(workspaceId, controller.signal)
      .then((data) => {
        if (controller.signal.aborted) return;
        setMap(data);
        setError(undefined);
      })
      .catch((reason: unknown) => {
        if (controller.signal.aborted || isAbortError(reason)) return;
        failed.current = true;
        setError(describeError(reason));
      })
      .finally(() => {
        if (!controller.signal.aborted) setRefreshing(false);
      });
    return () => controller.abort();
  }, [client, reload, workspaceId]);

  // While a version is being grouped, the map is read again every 2 s until the host is done.
  useEffect(() => {
    if (summarizing === null) return undefined;
    const timer = setTimeout(() => setReload((count) => count + 1), SUMMARY_POLL_MS);
    return () => clearTimeout(timer);
  }, [map, summarizing]);

  // A read that failed while the tunnel was down is tried again once it is back.
  useEffect(
    () =>
      client?.onStateChange((state) => {
        if (state === 'ready' && failed.current) setReload((count) => count + 1);
      }),
    [client],
  );

  const versions = map?.versions;
  const selected: WorkMapVersion | undefined = useMemo(
    () => versions?.find((version) => version.key === picked) ?? versions?.[0],
    [picked, versions],
  );
  const topic = selected?.topics && topicIndex !== undefined ? selected.topics[topicIndex] : undefined;

  const summarize = useCallback(async () => {
    if (!client || !workspaceId || !selected) return;
    setAsking(true);
    try {
      await client.summarizeWorkMap(workspaceId, selected.key);
      setReload((count) => count + 1);
    } catch (reason) {
      showToast(describeError(reason), 'error');
    } finally {
      setAsking(false);
    }
  }, [client, selected, workspaceId]);

  const pickVersion = (key: string) => {
    setPicked(key);
    setTopicIndex(undefined);
    setShowCommits(false);
  };

  const busy = summarizing !== null;
  const summarizingSelected = selected !== undefined && summarizing === selected.key;

  let body;
  if (!map) {
    body = error ? null : (
      <View style={styles.center}>
        <DialogLoading />
      </View>
    );
  } else if (!map.available) {
    body = <EmptyState title={t('workMap.title')} description={unavailableReason(map.reason)} />;
  } else if (!selected) {
    body = <EmptyState title={t('workMap.title')} description={t('workMap.status.unreleased')} />;
  } else {
    body = (
      <>
        <ScrollView
          horizontal
          showsHorizontalScrollIndicator={false}
          style={styles.chipsScroll}
          contentContainerStyle={styles.chips}
        >
          {map.versions.map((version) => (
            <VersionChip
              key={version.key}
              version={version}
              selected={version.key === selected.key}
              onPress={() => pickVersion(version.key)}
            />
          ))}
        </ScrollView>
        <ScrollView
          contentContainerStyle={[styles.content, { paddingBottom: insets.bottom + spacing.xxl }]}
          refreshControl={
            <RefreshControl
              refreshing={refreshing}
              tintColor={palette.accent}
              onRefresh={() => {
                setRefreshing(true);
                setReload((count) => count + 1);
              }}
            />
          }
        >
          <View style={styles.statusRow}>
            <Text style={styles.status}>{statusLabel(selected)}</Text>
            <Text style={styles.detail}>
              {[
                t('workMap.commits', { count: selected.total }),
                selected.topics ? t('workMap.topicsCount', { count: selected.topics.length }) : undefined,
              ]
                .filter(Boolean)
                .join(' · ')}
            </Text>
          </View>
          {selected.truncated ? (
            <Text style={styles.detail}>{t('workMap.truncated', { count: selected.commits.length })}</Text>
          ) : null}

          {selected.topics && selected.topics.length > 0 ? (
            <>
              {selected.topicsStale ? (
                <View style={styles.statusRow}>
                  <Text style={styles.stale}>{t('workMap.stale')}</Text>
                  <Button
                    label={t('workMap.summarizeAgain')}
                    compact
                    tone="neutral"
                    busy={asking || summarizingSelected}
                    disabled={busy}
                    onPress={() => void summarize()}
                  />
                </View>
              ) : null}
              {selected.topics.map((each, index) => (
                <TopicCard
                  key={`${each.title}-${index}`}
                  topic={each}
                  requests={each.requests.length}
                  onPress={() => setTopicIndex(index)}
                />
              ))}
            </>
          ) : (
            <Card style={styles.noTopics}>
              <Text style={styles.summary}>{t('workMap.noTopics')}</Text>
              {summarizingSelected ? (
                <View style={styles.statusRow}>
                  <ActivityIndicator color={palette.accent} />
                  <Text style={styles.detail}>{t('workMap.summarizing')}</Text>
                </View>
              ) : (
                <Button
                  label={t('workMap.summarize')}
                  compact
                  busy={asking}
                  disabled={busy}
                  style={styles.start}
                  onPress={() => void summarize()}
                />
              )}
              {selected.summaryError ? <ErrorBanner message={selected.summaryError} /> : null}
              {selected.commits.length > 0 ? (
                <Button
                  label={showCommits ? t('workMap.hideCommits') : t('workMap.showCommits')}
                  compact
                  tone="ghost"
                  style={styles.start}
                  onPress={() => setShowCommits((shown) => !shown)}
                />
              ) : null}
              {showCommits
                ? selected.commits.map((commit) => <CommitRow key={commit.sha} commit={commit} />)
                : null}
            </Card>
          )}
        </ScrollView>
      </>
    );
  }

  return (
    <View style={styles.screen}>
      <Stack.Screen options={{ title: t('workMap.title') }} />
      {error ? (
        <View style={styles.errorRow}>
          <ErrorBanner message={error} />
          <Button
            label={t('phone.files.retry')}
            compact
            onPress={() => setReload((count) => count + 1)}
            style={styles.retry}
          />
        </View>
      ) : null}
      {body}
      {topic && selected ? (
        <TopicSheet topic={topic} version={selected} onClose={() => setTopicIndex(undefined)} />
      ) : null}
    </View>
  );
}

function RatioBar({ counts }: { counts: WorkMapVersion['counts'] }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.bar}>
      {laneShares(counts).map((entry) => (
        <View key={entry.lane} style={{ backgroundColor: laneColor(palette, entry.lane), flex: entry.share }} />
      ))}
    </View>
  );
}

function VersionChip({
  version,
  selected,
  onPress,
}: {
  version: WorkMapVersion;
  selected: boolean;
  onPress: () => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={[styles.chip, selected && { borderColor: palette.accent, backgroundColor: palette.accentMuted }]}
    >
      <Text style={styles.chipName}>{versionName(version)}</Text>
      <RatioBar counts={version.counts} />
      <Text style={styles.chipMeta}>
        {chipDate(version)} · {version.total}
      </Text>
    </Pressable>
  );
}

function LaneDots({ lanes }: { lanes: readonly WorkMapTopic['lanes'][number][] }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.dots}>
      {LANE_ORDER.filter((lane) => lanes.includes(lane)).map((lane) => (
        <View
          key={lane}
          accessibilityLabel={laneLabel(lane)}
          style={[styles.dot, { backgroundColor: laneColor(palette, lane) }]}
        />
      ))}
    </View>
  );
}

function TopicCard({ topic, requests, onPress }: { topic: WorkMapTopic; requests: number; onPress: () => void }) {
  const styles = useStyles(makeStyles);
  const counts = [t('workMap.commits', { count: topic.commits.length })];
  if (requests > 0) counts.push(t('workMap.requestsCount', { count: requests }));
  return (
    <Pressable accessibilityRole="button" onPress={onPress}>
      <Card style={styles.topic}>
        <Text style={styles.topicTitle}>{topic.title}</Text>
        {topic.summary ? (
          <Text numberOfLines={2} style={styles.summary}>
            {topic.summary}
          </Text>
        ) : null}
        <View style={styles.statusRow}>
          <LaneDots lanes={topic.lanes} />
          <Text style={styles.detail}>{counts.join(' · ')}</Text>
        </View>
      </Card>
    </Pressable>
  );
}

function CommitRow({ commit }: { commit: WorkMapVersion['commits'][number] }) {
  const styles = useStyles(makeStyles);
  return (
    <Pressable
      accessibilityRole="button"
      onPress={() => {
        void Clipboard.setStringAsync(commit.sha).then(() => showToast(t('workMap.copied')));
      }}
      style={styles.commit}
    >
      <Text style={styles.sha}>{commit.sha}</Text>
      <Text style={styles.commitText}>
        {commit.scope ? <Text style={styles.scope}>{commit.scope}  </Text> : null}
        {commit.subject}
      </Text>
    </Pressable>
  );
}

function TopicSheet({
  topic,
  version,
  onClose,
}: {
  topic: WorkMapTopic;
  version: WorkMapVersion;
  onClose: () => void;
}) {
  const styles = useStyles(makeStyles);
  const commits = topicCommits(version, topic);
  const requests = topicRequests(version, topic);
  const files = commits.reduce((total, commit) => total + commit.filesCount, 0);
  return (
    <BottomSheet visible onClose={onClose} title={topic.title}>
      <LaneDots lanes={topic.lanes} />
      {topic.summary ? <Text style={styles.summary}>{topic.summary}</Text> : null}

      <Text style={styles.section}>{t('workMap.detail.requests')}</Text>
      {requests.length === 0 ? (
        <Text style={styles.detail}>{t('workMap.detail.noRequests')}</Text>
      ) : (
        requests.map((request) => (
          <Text key={request.id} style={styles.request}>
            {request.text}
          </Text>
        ))
      )}

      <Text style={styles.section}>{t('workMap.detail.commits')}</Text>
      {commits.map((commit) => (
        <CommitRow key={commit.sha} commit={commit} />
      ))}

      <Text style={styles.detail}>{t('workMap.detail.filesCount', { count: files })}</Text>
    </BottomSheet>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    center: { alignItems: 'center', flex: 1, justifyContent: 'center' },
    errorRow: { gap: spacing.lg, paddingBottom: spacing.lg },
    retry: { alignSelf: 'flex-start', marginHorizontal: spacing.xxl },
    chipsScroll: { flexGrow: 0 },
    chips: { gap: spacing.lg, padding: spacing.xl },
    chip: {
      backgroundColor: palette.surface,
      borderColor: palette.border,
      borderRadius: radius.md,
      borderWidth: StyleSheet.hairlineWidth,
      gap: spacing.md,
      minWidth: 96,
      padding: spacing.xl,
    },
    chipName: { color: palette.text, fontSize: 13, fontWeight: '700' },
    chipMeta: { color: palette.textMuted, fontSize: 10 },
    bar: {
      backgroundColor: palette.track,
      borderRadius: radius.round,
      flexDirection: 'row',
      height: 4,
      overflow: 'hidden',
    },
    content: { gap: spacing.lg, paddingHorizontal: spacing.xl },
    statusRow: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.lg },
    status: { color: palette.text, fontSize: 13, fontWeight: '600' },
    stale: { color: palette.warning, fontSize: 12 },
    detail: { color: palette.textMuted, fontSize: 12 },
    topic: { gap: spacing.md },
    topicTitle: { ...typeScale.heading, color: palette.text },
    summary: { color: palette.textMuted, fontSize: 13, lineHeight: 18 },
    dots: { flexDirection: 'row', gap: spacing.md },
    dot: { borderRadius: radius.round, height: 8, width: 8 },
    noTopics: { gap: spacing.lg },
    start: { alignSelf: 'flex-start' },
    commit: { flexDirection: 'row', gap: spacing.lg, paddingVertical: spacing.sm },
    sha: { color: palette.accent, fontFamily: monoFontFamily, fontSize: 12 },
    commitText: { color: palette.text, flex: 1, fontSize: 12 },
    scope: { color: palette.textMuted },
    section: { color: palette.textMuted, fontSize: 11, fontWeight: '600', marginTop: spacing.lg },
    request: {
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.md,
      color: palette.text,
      fontSize: 12,
      padding: spacing.lg,
    },
  });
