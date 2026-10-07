import { useCallback, useMemo, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { router } from 'expo-router';
import { describeError } from '@/api/client';
import { isAgentIOPane, type MobileSessionSummary, type MobileState } from '@/api/types';
import { NewSessionSheet, type NewSessionChoice } from '@/components/new-session-sheet';
import { SessionRow } from '@/components/session-card';
import { Button, EmptyState } from '@/components/ui';
import { useCapabilities } from '@/hooks/use-capabilities';
import { hasCapability } from '@/lib/capabilities';
import { t } from '@/lib/i18n';
import { groupSessionsByWorkspace } from '@/lib/merge';
import { useHostClient } from '@/store/live';
import { showToast } from '@/store/toast';
import { cardShadow, headingFontFamily, monoFontFamily, spacing, useStyles, type Palette } from '@/theme';

/** Opens a pane, or says why an agent's own terminal or browser stays on the Mac. */
export function openSession(hostId: string, session: MobileSessionSummary): void {
  // An agent's own terminal/browser pane is listed, not opened: its contents live on the
  // Mac and the host keeps no record to show here.
  if (isAgentIOPane(session.kind)) showToast(t('phone.workspaces.agentPaneOnly'));
  else router.push(`/host/${hostId}/session/${session.id}`);
}

/**
 * One host's workspaces as sections: the workspace name in bold with its path in mono,
 * 파일 and 새 창 beside it, then one plain list with a row per pane. Used by the "현황" tab
 * (with `glance`, so rows may show context and elapsed time), the "세션" tab and the
 * host screen. Creating a pane opens it; `onCreated` lets a screen read the host again.
 */
export function HostWorkspaces({
  hostId,
  state,
  loading,
  glance = false,
  onCreated,
}: {
  hostId: string;
  state: MobileState | undefined;
  loading: boolean;
  glance?: boolean;
  onCreated?: () => void;
}) {
  const styles = useStyles(makeStyles);
  const client = useHostClient(hostId);
  // A Mac older than the files routes never shows the button.
  const canBrowseFiles = hasCapability(useCapabilities(hostId, client), 'files');
  const [creatingFor, setCreatingFor] = useState<string | undefined>(undefined);
  const [creating, setCreating] = useState(false);

  const groups = useMemo(() => (state ? groupSessionsByWorkspace(state) : []), [state]);
  const activeWorkspace = groups.find((group) => group.workspace.id === creatingFor);

  const createSession = useCallback(
    async (choice: NewSessionChoice) => {
      if (!client || !creatingFor) return;
      setCreating(true);
      try {
        const created = await client.createSession(creatingFor, choice);
        setCreatingFor(undefined);
        onCreated?.();
        router.push(`/host/${hostId}/session/${created.sessionId}`);
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        setCreating(false);
      }
    },
    [client, creatingFor, hostId, onCreated],
  );

  return (
    <View style={styles.groups}>
      {groups.length === 0 ? (
        <EmptyState
          title={loading ? t('phone.workspaces.loading') : t('phone.workspaces.empty.title')}
          description={t('phone.workspaces.empty.description')}
        />
      ) : (
        groups.map((group) => (
          <View key={group.workspace.id} style={styles.group}>
            <View style={styles.groupHeader}>
              <View style={styles.groupTitles}>
                <Text numberOfLines={1} style={styles.workspaceName}>
                  {group.workspace.name}
                </Text>
                <Text numberOfLines={1} ellipsizeMode="head" style={styles.workspacePath}>
                  {group.workspace.path}
                </Text>
              </View>
              {canBrowseFiles ? (
                <Button
                  label={t('phone.files.open')}
                  accessibilityLabel={t('phone.files.openLabel', { name: group.workspace.name })}
                  compact
                  onPress={() => router.push(`/host/${hostId}/workspace/${group.workspace.id}/files`)}
                />
              ) : null}
              <Button
                label={t('phone.workspaces.newPane')}
                compact
                tone="ink"
                onPress={() => setCreatingFor(group.workspace.id)}
              />
            </View>

            {group.sessions.length === 0 ? (
              <Text style={styles.noSessions}>{t('phone.workspaces.noSessions')}</Text>
            ) : (
              <View style={styles.list}>
                <View style={styles.listClip}>
                  {group.sessions.map((session, index) => (
                    <SessionRow
                      key={session.id}
                      hostId={hostId}
                      session={session}
                      glance={glance}
                      first={index === 0}
                      onPress={() => openSession(hostId, session)}
                    />
                  ))}
                </View>
              </View>
            )}
          </View>
        ))
      )}

      <NewSessionSheet
        visible={creatingFor !== undefined}
        workspaceName={activeWorkspace?.workspace.name ?? ''}
        busy={creating}
        onCancel={() => setCreatingFor(undefined)}
        onCreate={(choice) => void createSession(choice)}
      />
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    groups: { gap: spacing.lg },
    group: { gap: spacing.sm },
    groupHeader: {
      alignItems: 'center',
      flexDirection: 'row',
      gap: spacing.md,
      paddingHorizontal: spacing.xs,
      paddingTop: spacing.xs,
    },
    groupTitles: { alignItems: 'baseline', flex: 1, flexDirection: 'row', gap: spacing.md },
    workspaceName: {
      color: palette.text,
      flexShrink: 0,
      fontFamily: headingFontFamily,
      fontSize: 16,
      fontWeight: '700',
      maxWidth: '60%',
    },
    workspacePath: { color: palette.textFaint, flex: 1, fontFamily: monoFontFamily, fontSize: 12 },
    // The shadow on the outside, the clip on the inside: a clipped view casts no iOS shadow.
    list: { ...cardShadow, backgroundColor: palette.surface, borderRadius: 18 },
    listClip: { borderRadius: 18, overflow: 'hidden' },
    noSessions: {
      color: palette.textFaint,
      fontSize: 13,
      paddingHorizontal: spacing.xs,
      paddingVertical: spacing.sm,
    },
  });
