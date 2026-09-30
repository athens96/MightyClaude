import { ScrollView, StyleSheet, Text, View } from 'react-native';
import { router } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { Button } from '@/components/ui';
import { t } from '@/lib/i18n';
import { CONNECT_STEPS } from '@/lib/onboarding';
import { radius, serifFontFamily, spacing, typeScale, useStyles, type Palette } from '@/theme';

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
      gap: spacing.xxl,
      paddingHorizontal: spacing.xl,
      paddingTop: spacing.xl,
    },
    title: {
      ...typeScale.display,
      color: palette.text,
      textAlign: 'center',
    },
    steps: { gap: spacing.xl },
    step: { alignItems: 'flex-start', flexDirection: 'row', gap: spacing.md },
    // The step number is a serif numeral in a hairline ring — a page marker, not a button.
    badge: {
      alignItems: 'center',
      borderColor: palette.border,
      borderRadius: radius.round,
      borderWidth: StyleSheet.hairlineWidth,
      height: 32,
      justifyContent: 'center',
      width: 32,
    },
    badgeText: { color: palette.textMuted, fontFamily: serifFontFamily, fontSize: 16 },
    stepContent: { flex: 1, gap: spacing.xs, paddingTop: 5 },
    stepTitle: { color: palette.text, fontSize: 16, fontWeight: '600' },
    stepBody: { color: palette.textMuted, fontSize: 14, lineHeight: 21 },
  });
