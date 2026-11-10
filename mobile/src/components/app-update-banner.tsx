import { useCallback, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { ApiError, describeError } from '@/api/client';
import type { MobileAppUpdate } from '@/api/types';
import { ConfirmDialog } from '@/components/sheets';
import { Button } from '@/components/ui';
import { appUpdateBanner, appUpdateConfirm, canCheckAppUpdate } from '@/lib/app-update';
import { t } from '@/lib/i18n';
import { useHostClient } from '@/store/live';
import { showToast } from '@/store/toast';
import { radius, spacing, useStyles, type Palette } from '@/theme';

/**
 * The desktop app's update above the host's session list (docs/phone-desktop-update.md).
 * Install asks first, then sends `confirm: true`; the host quits to install, so a dropped
 * connection after that is expected and the link layer's reconnect handles it quietly.
 */
export function AppUpdateBanner({
  hostId,
  hostName,
  update,
}: {
  hostId: string;
  hostName: string;
  update: MobileAppUpdate | undefined;
}) {
  const styles = useStyles(makeStyles);
  const client = useHostClient(hostId);
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [open, setOpen] = useState(false);

  const send = useCallback(
    async (action: 'check' | 'install') => {
      if (!client) return;
      setBusy(true);
      try {
        const result = await client.appUpdate(action, action === 'install' ? true : undefined);
        if (action === 'check' && result.phase === 'upToDate') {
          showToast(t('phone.appUpdate.upToDate', { current: result.current }));
        }
      } catch (error) {
        // The Mac quits to install, so the request may end without an answer: not an error.
        if (action === 'install' && !(error instanceof ApiError)) return;
        showToast(describeError(error), 'error');
      } finally {
        setBusy(false);
        setConfirming(false);
      }
    },
    [client],
  );

  const banner = appUpdateBanner(update);
  if (update && canCheckAppUpdate(update)) {
    // Nothing to report: a quiet entry to look for an update by hand.
    return (
      <View style={styles.quiet}>
        <Button
          label={t('phone.appUpdate.check')}
          accessibilityLabel={`${t('phone.appUpdate.title')}: ${t('phone.appUpdate.check')}`}
          compact
          busy={busy}
          onPress={() => void send('check')}
        />
      </View>
    );
  }
  if (!update || !banner) return null;
  const confirm = appUpdateConfirm(update, hostName);
  const hasNotes = Boolean(update.notes);

  return (
    <View style={styles.banner}>
      <Pressable
        accessibilityRole="button"
        disabled={!hasNotes}
        onPress={() => setOpen((value) => !value)}
        style={styles.row}
      >
        <Text style={styles.text}>{t(banner.key, banner.params)}</Text>
        {banner.action ? (
          <Button
            label={t(banner.action === 'install' ? 'phone.appUpdate.install' : 'phone.appUpdate.check')}
            compact
            busy={busy}
            onPress={() => (banner.action === 'install' ? setConfirming(true) : void send('check'))}
          />
        ) : null}
      </Pressable>
      {open && update.notes ? <Text style={styles.notes}>{update.notes}</Text> : null}
      <ConfirmDialog
        visible={confirming}
        title={t('phone.appUpdate.confirmTitle')}
        description={t(confirm.key, confirm.params)}
        cancelLabel={t('phone.appUpdate.cancel')}
        confirmLabel={t('phone.appUpdate.confirm')}
        busy={busy}
        onConfirm={() => void send('install')}
        onCancel={() => setConfirming(false)}
      />
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    banner: {
      backgroundColor: palette.surfaceRaised,
      borderColor: palette.border,
      borderRadius: radius.md,
      borderWidth: StyleSheet.hairlineWidth,
      marginHorizontal: spacing.lg,
      marginTop: spacing.sm,
      padding: spacing.md,
    },
    quiet: { alignItems: 'flex-start', marginHorizontal: spacing.lg, marginTop: spacing.sm },
    row: { alignItems: 'center', flexDirection: 'row', gap: spacing.md },
    text: { color: palette.text, flex: 1, fontSize: 13, fontWeight: '600', lineHeight: 18 },
    notes: { color: palette.textMuted, fontSize: 12, lineHeight: 17, marginTop: spacing.sm },
  });
