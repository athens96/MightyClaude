import { useCallback, useEffect, useRef, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { describeError, type MobileClient } from '@/api/client';
import type { MobilePaneWorktree, PaneWorktreeAction } from '@/api/types';
import { Icon } from '@/components/icons';
import { BottomSheet, ConfirmDialog, SheetRow } from '@/components/sheets';
import { Button } from '@/components/ui';
import { t } from '@/lib/i18n';
import {
  menuOffers,
  primaryOffers,
  worktreeCounts,
  worktreeLabel,
  worktreeMarker,
  worktreeSetup,
  type WorktreeOffer,
} from '@/lib/pane-worktree';
import { showToast } from '@/store/toast';
import { radius, spacing, usePalette, useStyles, type Palette } from '@/theme';

/**
 * The Mac's worktree bar for a pane that works in its own worktree (`MobilePaneWorktree`): its branch → base, PORT, the
 * counts and setup state the Mac read, the main merge button (커밋하고 병합 while there are changes, 충돌 해결 요청 after
 * a conflict) and a menu with the other merge, undo and discard. Every action is the Mac's: the phone sends it and shows
 * the Mac's answer, which the Mac also wrote in the pane. A discard asks first; the Mac refuses it while children branch
 * from the pane. Opening the pane asks the Mac to read the counts again (`refresh`), and so does each settled run.
 */
export function PaneWorktreeBar({
  sessionId,
  worktree,
  client,
  running,
  onDiscarded,
}: {
  sessionId: string;
  worktree: MobilePaneWorktree;
  client: MobileClient | undefined;
  /** The pane's run: counts are read again when it settles. */
  running: boolean;
  /** The pane is gone after a discard. */
  onDiscarded: () => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [sending, setSending] = useState<PaneWorktreeAction | undefined>(undefined);
  const [menuOpen, setMenuOpen] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const sendingRef = useRef(false);

  // The Mac reads the counts every 20 s while its own bar is on screen; a pane opened only here asks once on open and
  // after each run.
  useEffect(() => {
    if (!client || running) return;
    const controller = new AbortController();
    client.paneWorktree(sessionId, 'refresh', controller.signal).catch(() => undefined);
    return () => controller.abort();
  }, [client, sessionId, running]);

  const perform = useCallback(
    async (action: PaneWorktreeAction) => {
      if (!client || sendingRef.current) return;
      sendingRef.current = true;
      setSending(action);
      try {
        const result = await client.paneWorktree(sessionId, action);
        if (result.message) showToast(result.message, result.done ? 'success' : 'error');
        if (action === 'discard' && result.done) onDiscarded();
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        sendingRef.current = false;
        setSending(undefined);
      }
    },
    [client, sessionId, onDiscarded],
  );

  const choose = (offer: WorktreeOffer) => {
    setMenuOpen(false);
    if (offer.disabledReason) {
      showToast(offer.disabledReason, 'error');
      return;
    }
    if (offer.confirm) setConfirming(true);
    else void perform(offer.action);
  };

  const busy = worktree.busy === true || sending !== undefined;
  const counts = worktreeCounts(worktree);
  const setup = worktreeSetup(worktree);

  return (
    <View accessible={false} style={styles.bar}>
      <View accessible accessibilityLabel={worktreeMarker(worktree)} style={styles.head}>
        <Icon name="branch" color={palette.accent} size={14} />
        <Text numberOfLines={1} ellipsizeMode="middle" style={styles.branch}>
          {worktreeLabel(worktree)}
        </Text>
        <Text style={styles.port}>PORT {worktree.port}</Text>
      </View>
      {counts || setup ? (
        <Text numberOfLines={1} style={styles.counts}>
          {counts}
          {counts && setup ? ' · ' : ''}
          {setup ? <Text style={setup.failed ? styles.failed : undefined}>{setup.text}</Text> : null}
        </Text>
      ) : null}
      <View style={styles.actions}>
        {primaryOffers(worktree).map((offer) => (
          <Button
            key={offer.action}
            label={offer.label}
            compact
            tone={offer.action === 'resolveConflict' ? 'neutral' : 'primary'}
            busy={sending === offer.action}
            disabled={busy || !client}
            onPress={() => choose(offer)}
          />
        ))}
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t('paneWorktree.bar.more')}
          disabled={busy || !client}
          hitSlop={6}
          onPress={() => setMenuOpen(true)}
          style={({ pressed }) => [styles.more, pressed && styles.pressed, (busy || !client) && styles.dim]}
        >
          <Text style={styles.moreText}>⋯</Text>
        </Pressable>
      </View>

      <BottomSheet visible={menuOpen} onClose={() => setMenuOpen(false)} title={t('paneWorktree.bar.more')} inset={false}>
        {menuOffers(worktree).map((offer) => (
          <SheetRow
            key={offer.action}
            label={offer.label}
            {...(offer.disabledReason ? { caption: offer.disabledReason } : {})}
            danger={offer.action === 'discard'}
            disabled={offer.disabledReason !== undefined}
            onPress={() => choose(offer)}
          />
        ))}
      </BottomSheet>

      <ConfirmDialog
        visible={confirming}
        title={t('paneWorktree.discard.title')}
        description={t('paneWorktree.discard.detail', { branch: worktree.branch })}
        confirmLabel={t('paneWorktree.discard.confirm')}
        destructive
        busy={sending === 'discard'}
        onConfirm={() => {
          setConfirming(false);
          void perform('discard');
        }}
        onCancel={() => setConfirming(false)}
      />
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    bar: {
      backgroundColor: palette.accentMuted,
      borderColor: palette.border,
      borderRadius: radius.md,
      borderWidth: 1,
      gap: spacing.sm,
      marginHorizontal: spacing.xl,
      marginTop: spacing.md,
      paddingHorizontal: spacing.xl,
      paddingVertical: spacing.lg,
    },
    head: { alignItems: 'center', flexDirection: 'row', gap: spacing.md },
    branch: { color: palette.text, flexShrink: 1, fontSize: 13, fontWeight: '600' },
    port: { color: palette.textMuted, fontSize: 12, fontVariant: ['tabular-nums'] },
    counts: { color: palette.textMuted, fontSize: 12 },
    failed: { color: palette.danger },
    actions: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.lg },
    more: {
      alignItems: 'center',
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: 1,
      justifyContent: 'center',
      paddingHorizontal: spacing.lg,
      paddingVertical: spacing.xs,
    },
    moreText: { color: palette.text, fontSize: 16, fontWeight: '700' },
    pressed: { backgroundColor: palette.surfaceRaised },
    dim: { opacity: 0.5 },
  });
