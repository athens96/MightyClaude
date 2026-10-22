import { useEffect, useState, useSyncExternalStore } from 'react';
import {
  AppState,
  Linking,
  PermissionsAndroid,
  Platform,
  ScrollView,
  StyleSheet,
  Switch,
  Text,
  View,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { Button, Card } from '@/components/ui';
import { t } from '@/lib/i18n';
import { PUSH_KINDS, type PushKind } from '@/lib/push/message';
import { pushSettings } from '@/lib/push/settings';
import { spacing, typeScale, usePalette, useStyles, type Palette } from '@/theme';

const KIND_LABEL: Record<PushKind, string> = {
  completed: 'phone.settings.kind.completed',
  failed: 'phone.settings.kind.failed',
  permission: 'phone.settings.kind.permission',
  question: 'phone.settings.kind.question',
  plan: 'phone.settings.kind.plan',
  signin: 'phone.settings.kind.signin',
};

/** Whether Android lets this app post notifications (Android 13 and later ask for it). */
async function notificationsAllowed(): Promise<boolean> {
  if (Platform.OS !== 'android' || Number(Platform.Version) < 33) return true;
  return PermissionsAndroid.check(PermissionsAndroid.PERMISSIONS.POST_NOTIFICATIONS);
}

function SettingSwitch({
  label,
  value,
  onChange,
}: {
  label: string;
  value: boolean;
  onChange: (value: boolean) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.row}>
      <Text style={styles.label}>{label}</Text>
      <Switch
        value={value}
        onValueChange={onChange}
        trackColor={{ false: palette.track, true: palette.accent }}
        accessibilityLabel={label}
      />
    </View>
  );
}

/** Which notifications the paired hosts send, and whether crash reports go out. */
export default function SettingsScreen() {
  const styles = useStyles(makeStyles);
  const insets = useSafeAreaInsets();
  const settings = useSyncExternalStore(pushSettings.subscribe, pushSettings.get);
  const [allowed, setAllowed] = useState(true);

  // The user may turn notifications on in the phone's settings and come back.
  useEffect(() => {
    const check = () => {
      notificationsAllowed().then(setAllowed).catch(() => undefined);
    };
    check();
    const subscription = AppState.addEventListener('change', (state) => {
      if (state === 'active') check();
    });
    return () => subscription.remove();
  }, []);

  const setKind = (kind: PushKind, value: boolean) => {
    void pushSettings.set({ ...settings, kinds: { ...settings.kinds, [kind]: value } });
  };

  return (
    <ScrollView
      contentContainerStyle={[styles.page, { paddingBottom: insets.bottom + spacing.xl }]}
    >
      <Card>
        <Text style={styles.heading}>{t('phone.settings.notifications')}</Text>
        <Text style={styles.hint}>{t('phone.settings.notificationsHint')}</Text>
        {!allowed ? (
          <View style={styles.block}>
            <Text style={styles.warning}>{t('phone.settings.permissionOff')}</Text>
            <Button
              label={t('phone.settings.openSystem')}
              tone="neutral"
              onPress={() => void Linking.openSettings()}
            />
          </View>
        ) : null}
        {PUSH_KINDS.map((kind) => (
          <SettingSwitch
            key={kind}
            label={t(KIND_LABEL[kind])}
            value={settings.kinds[kind]}
            onChange={(value) => setKind(kind, value)}
          />
        ))}
      </Card>
      <Card>
        <SettingSwitch
          label={t('phone.settings.crashReports')}
          value={settings.crashReports}
          onChange={(value) => void pushSettings.set({ ...settings, crashReports: value })}
        />
        <Text style={styles.hint}>{t('phone.settings.crashReportsHint')}</Text>
      </Card>
    </ScrollView>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    page: {
      backgroundColor: palette.background,
      flexGrow: 1,
      gap: spacing.xl,
      paddingHorizontal: spacing.xl,
      paddingTop: spacing.xl,
    },
    block: { gap: spacing.md, paddingVertical: spacing.md },
    heading: { ...typeScale.heading, color: palette.text },
    hint: { ...typeScale.caption, color: palette.textMuted, paddingTop: spacing.sm },
    warning: { ...typeScale.body, color: palette.warning },
    row: {
      alignItems: 'center',
      flexDirection: 'row',
      gap: spacing.lg,
      justifyContent: 'space-between',
      paddingVertical: spacing.md,
    },
    label: { ...typeScale.body, color: palette.text, flexShrink: 1 },
  });
