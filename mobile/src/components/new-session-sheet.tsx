import { useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import type { Provider, SessionKind } from '@/api/types';
import { BottomSheet, DialogButton } from '@/components/sheets';
import { Chip } from '@/components/ui';
import { t } from '@/lib/i18n';
import { kindLabel, providerIsBeta, providerLabel, spacing, useStyles, usePalette, type Palette } from '@/theme';

const PROVIDERS: Provider[] = ['claude', 'codex', 'gemini'];
/** pi is offered only by a host that advertises the `pi` capability. */
const PI: Provider = 'pi';

export interface NewSessionChoice {
  kind: SessionKind;
  provider?: Provider;
}

export function NewSessionSheet({
  visible,
  workspaceName,
  busy,
  antigravity = true,
  offersPi = false,
  onCancel,
  onCreate,
}: {
  visible: boolean;
  workspaceName: string;
  busy: boolean;
  /** The host runs Antigravity for `gemini` (`hostRunsAntigravity`); else it is named Gemini. */
  antigravity?: boolean;
  /** The host advertises the `pi` capability. */
  offersPi?: boolean;
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
    <BottomSheet
      visible={visible}
      onClose={onCancel}
      title={`${t('phone.workspaces.newPane')} · ${workspaceName}`}
      footer={
        <>
          <DialogButton label={t('common.cancel')} onPress={onCancel} />
          <DialogButton
            label={t('phone.newPane.create')}
            kind="primary"
            busy={busy}
            onPress={() =>
              onCreate(chosenKind === 'claude' ? { kind: chosenKind, provider } : { kind: chosenKind })
            }
          />
        </>
      }
    >
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
            {(offersPi ? [...PROVIDERS, PI] : PROVIDERS).map((value) => (
              <Chip
                key={value}
                label={providerLabel(value, antigravity)}
                beta={providerIsBeta(value)}
                color={palette.accent}
                selected={provider === value}
                onPress={() => setProvider(value)}
              />
            ))}
          </View>
        </>
      ) : null}
    </BottomSheet>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    label: { color: palette.textMuted, fontSize: 13, fontWeight: '500' },
    chips: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
  });
