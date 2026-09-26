import { ScrollView, StyleSheet, Text, View } from 'react-native';
import { router } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { Button } from '@/components/ui';
import { t } from '@/lib/i18n';
import { CONNECT_STEPS } from '@/lib/onboarding';
import { radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

function StepRow({ index, titleKey, bodyKey }: { index: number; titleKey: string; bodyKey: string }) {
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.step}>
      <View style={styles.badge}>
        <Text style={styles.badgeText}>{index + 1}</Text>
      </View>
      <View style={styles.stepContent}>
        <Text style={styles.stepTitle}>{t(titleKey)}</Text>
        <Text style={styles.stepBody}>{t(bodyKey)}</Text>
      </View>
    </View>
  );
}

export default function ConnectScreen() {
  const styles = useStyles(makeStyles);
  const insets = useSafeAreaInsets();

  return (
    <ScrollView
      contentContainerStyle={[styles.container, { paddingBottom: insets.bottom + spacing.xl }]}
    >
      <Text style={styles.title}>{t('phone.connect.title')}</Text>
      <View style={styles.steps}>
        {CONNECT_STEPS.map((step, i) => (
          <StepRow key={step.titleKey} index={i} titleKey={step.titleKey} bodyKey={step.bodyKey} />
        ))}
      </View>
      <Button
        label={t('phone.connect.scan')}
        tone="primary"
        onPress={() => router.push('/pair')}
      />
    </ScrollView>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    container: {
      gap: spacing.xl,
      padding: spacing.lg,
    },
    title: {
      color: palette.text,
      fontSize: 22,
      fontWeight: '700',
      textAlign: 'center',
    },
    steps: { gap: spacing.lg },
    step: { alignItems: 'flex-start', flexDirection: 'row', gap: spacing.md },
    badge: {
      alignItems: 'center',
      backgroundColor: palette.accent,
      borderRadius: 999,
      height: 32,
      justifyContent: 'center',
      width: 32,
    },
    badgeText: { color: '#fff', fontSize: 15, fontWeight: '700' },
    stepContent: { flex: 1, gap: spacing.xs },
    stepTitle: { color: palette.text, fontSize: 16, fontWeight: '600' },
    stepBody: { color: palette.textMuted, fontSize: 14 },
  });
