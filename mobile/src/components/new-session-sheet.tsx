import { useState } from 'react';
import { Modal, StyleSheet, Text, View } from 'react-native';
import type { Provider, SessionKind } from '@/api/types';
import { Button, Chip } from '@/components/ui';
import { kindLabel, providerLabel, radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

const PROVIDERS: Provider[] = ['claude', 'codex', 'gemini'];

export interface NewSessionChoice {
  kind: SessionKind;
  provider?: Provider;
}

export function NewSessionSheet({
  visible,
  workspaceName,
  workspaceRemote,
  busy,
  onCancel,
  onCreate,
}: {
  visible: boolean;
  workspaceName: string;
  /** A local workspace cannot host a shell pane the phone could use. */
  workspaceRemote: boolean;
  busy: boolean;
  onCancel: () => void;
  onCreate: (choice: NewSessionChoice) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [kind, setKind] = useState<SessionKind>('claude');
  const [provider, setProvider] = useState<Provider>('claude');

  // The host answers 409 for `shell` on a local workspace, so the option is not offered.
  const kinds: SessionKind[] = workspaceRemote ? ['claude', 'shell'] : ['claude'];
  const chosenKind = kinds.includes(kind) ? kind : 'claude';

  return (
    <Modal visible={visible} transparent animationType="fade" onRequestClose={onCancel}>
      <View style={styles.backdrop}>
        <View style={styles.sheet}>
          <Text style={styles.title}>새 창 · {workspaceName}</Text>

          {kinds.length > 1 ? (
            <>
              <Text style={styles.label}>종류</Text>
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
              <Text style={styles.label}>프로바이더</Text>
              <View style={styles.chips}>
                {PROVIDERS.map((value) => (
                  <Chip
                    key={value}
                    label={providerLabel(value)}
                    color={palette.accent}
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
      borderColor: palette.border,
      borderRadius: radius.lg,
      borderWidth: StyleSheet.hairlineWidth,
      gap: spacing.md,
      padding: spacing.lg,
    },
    title: { color: palette.text, fontSize: 16, fontWeight: '700' },
    label: { color: palette.textMuted, fontSize: 13 },
    chips: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
    actions: { flexDirection: 'row', gap: spacing.sm, marginTop: spacing.sm },
    action: { flex: 1 },
  });
