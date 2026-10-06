import { useState } from 'react';
import { Modal, StyleSheet, Text, View } from 'react-native';
import type { Provider, SessionKind } from '@/api/types';
import { Button, Chip } from '@/components/ui';
import { t } from '@/lib/i18n';
import { kindLabel, providerIsBeta, providerLabel, radius, spacing, typeScale, useStyles, usePalette, type Palette } from '@/theme';

const PROVIDERS: Provider[] = ['claude', 'codex', 'gemini'];

export interface NewSessionChoice {
  kind: SessionKind;
  provider?: Provider;
}

export function NewSessionSheet({
  visible,
  workspaceName,
  busy,
  onCancel,
  onCreate,
}: {
  visible: boolean;
  workspaceName: string;
  busy: boolean;
  onCancel: () => void;
  onCreate: (choice: NewSessionChoice) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [kind, setKind] = useState<SessionKind>('claude');
  const [provider, setProvider] = useState<Provider>('claude');

  const kinds: SessionKind[] = ['claude'];
  const chosenKind = kinds.includes(kind) ? kind : 'claude';

  return (
    <Modal visible={visible} transparent animationType="fade" onRequestClose={onCancel}>
      <View style={styles.backdrop}>
        <View style={styles.sheet}>
          <Text style={styles.title}>
            {t('phone.workspaces.newPane')} · {workspaceName}
          </Text>

          {kinds.length > 1 ? (
            <>
              <Text style={styles.label}>{t('phone.newPane.kind')}</Text>
              <View style={styles.chips}>
                {kinds.map((value) => (
                  <Chip
                    key={value}
                    label={kindLabel(value)}
                    color={palette.accent}
                    selected={chosenKind === value}
                    onPress={() => setKind(value)}
                  />
                ))}
              </View>
            </>
          ) : null}

          {chosenKind === 'claude' ? (
            <>
              <Text style={styles.label}>{t('phone.newPane.provider')}</Text>
              <View style={styles.chips}>
                {PROVIDERS.map((value) => (
                  <Chip
                    key={value}
                    label={providerLabel(value)}
                    beta={providerIsBeta(value)}
                    color={palette.accent}
                    selected={provider === value}
                    onPress={() => setProvider(value)}
                  />
                ))}
              </View>
            </>
          ) : null}

          <View style={styles.actions}>
            <Button label={t('common.cancel')} tone="ghost" onPress={onCancel} style={styles.action} />
            <Button
              label={t('phone.newPane.create')}
              tone="primary"
              busy={busy}
              style={styles.action}
              onPress={() =>
                onCreate(chosenKind === 'claude' ? { kind: chosenKind, provider } : { kind: chosenKind })
              }
            />
          </View>
        </View>
      </View>
    </Modal>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    backdrop: {
      backgroundColor: palette.overlay,
      flex: 1,
      justifyContent: 'center',
      padding: spacing.lg,
    },
    sheet: {
      backgroundColor: palette.surface,
      borderRadius: radius.lg,
      gap: spacing.md,
      padding: spacing.xl,
    },
    title: { ...typeScale.heading, color: palette.text, fontSize: 20, lineHeight: 26 },
    label: { color: palette.textMuted, fontSize: 13, fontWeight: '500' },
    chips: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
    actions: { flexDirection: 'row', gap: spacing.sm, marginTop: spacing.sm },
    action: { flex: 1 },
  });
