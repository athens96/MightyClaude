import { useEffect, useState, useSyncExternalStore } from 'react';
import { AppState, Linking, PermissionsAndroid, Platform, ScrollView, StyleSheet } from 'react-native';
import Constants from 'expo-constants';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { SettingRow, SettingSwitch, SettingsSection } from '@/components/settings-rows';
import { t } from '@/lib/i18n';
import { PUSH_KINDS, type PushKind } from '@/lib/push/message';
import { pushSettings } from '@/lib/push/settings';
import { spacing, useStyles, type Palette } from '@/theme';

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

/** Which notifications the paired hosts send, whether crash reports go out, and the app's version. */
export default function SettingsScreen() {
  const styles = useStyles(makeStyles);
  const insets = useSafeAreaInsets();
  const settings = useSyncExternalStore(pushSettings.subscribe, pushSettings.get);
  const [allowed, setAllowed] = useState(true);
  const version = Constants.expoConfig?.version;

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

  // Each change is built on the store's latest value, so two quick toggles both stay.
  const setKind = (kind: PushKind, value: boolean) => {
    void pushSettings.update((now) => ({ ...now, kinds: { ...now.kinds, [kind]: value } }));
  };
  const setCrashReports = (value: boolean) => {
    void pushSettings.update((now) => ({ ...now, crashReports: value }));
  };

  return (
    <ScrollView
      contentContainerStyle={[styles.page, { paddingBottom: insets.bottom + spacing.xl }]}
    >
      <SettingsSection
        title={t('phone.settings.notifications')}
        footnote={t('phone.settings.notificationsHint')}
      >
        {!allowed ? (
          <SettingRow
            label={t('phone.settings.openSystem')}
            caption={t('phone.settings.permissionOff')}
            warning
            onPress={() => void Linking.openSettings()}
          />
        ) : null}
        {PUSH_KINDS.map((kind) => (
          <SettingSwitch
            key={kind}
            label={t(KIND_LABEL[kind])}
            value={settings.kinds[kind]}
            onChange={(value) => setKind(kind, value)}
          />
        ))}
      </SettingsSection>
      <SettingsSection title={t('settings.appInfo.sectionTitle')}>
        <SettingSwitch
          label={t('phone.settings.crashReports')}
          caption={t('phone.settings.crashReportsHint')}
          value={settings.crashReports}
          onChange={setCrashReports}
        />
        {version ? <SettingRow label={t('settings.appInfo.versionLabel')} value={version} /> : null}
      </SettingsSection>
    </ScrollView>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    page: {
      backgroundColor: palette.background,
      flexGrow: 1,
      gap: spacing.xxl,
      paddingHorizontal: spacing.xl,
      paddingTop: spacing.xl,
    },
  });
