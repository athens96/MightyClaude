import { useState } from 'react';
import { Modal, StyleSheet, Text, View } from 'react-native';
import type { Provider, SessionKind } from '@/api/types';
import { Button, Chip } from '@/components/ui';
import { colors, kindLabels, providerLabels, radius, spacing } from '@/theme';

const KINDS: SessionKind[] = ['claude', 'shell'];
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
  const [kind, setKind] = useState<SessionKind>('claude');
  const [provider, setProvider] = useState<Provider>('claude');

  return (
    <Modal visible={visible} transparent animationType="fade" onRequestClose={onCancel}>
      <View style={styles.backdrop}>
        <View style={styles.sheet}>
          <Text style={styles.title}>새 창 · {workspaceName}</Text>

          <Text style={styles.label}>종류</Text>
          <View style={styles.chips}>
            {KINDS.map((value) => (
              <Chip
                key={value}
                label={kindLabels[value]}
                color={colors.accent}
                selected={kind === value}
                onPress={() => setKind(value)}
              />
            ))}
          </View>

          {kind === 'claude' ? (
            <>
              <Text style={styles.label}>프로바이더</Text>
              <View style={styles.chips}>
                {PROVIDERS.map((value) => (
                  <Chip
                    key={value}
                    label={providerLabels[value]}
                    color={colors.accent}
                    selected={provider === value}
                    onPress={() => setProvider(value)}
                  />
                ))}
              </View>
            </>
          ) : null}

          <View style={styles.actions}>
            <Button label="취소" tone="ghost" onPress={onCancel} style={styles.action} />
            <Button
              label="만들기"
              tone="primary"
              busy={busy}
              style={styles.action}
              onPress={() =>
                onCreate(kind === 'claude' ? { kind, provider } : { kind })
              }
            />
          </View>
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  backdrop: {
    backgroundColor: 'rgba(0,0,0,0.6)',
    flex: 1,
    justifyContent: 'center',
    padding: spacing.lg,
  },
  sheet: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: radius.lg,
    borderWidth: StyleSheet.hairlineWidth,
    gap: spacing.md,
    padding: spacing.lg,
  },
  title: { color: colors.text, fontSize: 16, fontWeight: '700' },
  label: { color: colors.textMuted, fontSize: 13 },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  actions: { flexDirection: 'row', gap: spacing.sm, marginTop: spacing.sm },
  action: { flex: 1 },
});
