import { useCallback, useState } from 'react';
import {
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { CameraView, useCameraPermissions } from 'expo-camera';
import { router } from 'expo-router';
import { Button, Card, Chip, ErrorBanner } from '@/components/ui';
import { describeError, probeHost } from '@/api/client';
import { parsePairingUrl, relayTransportError, type PairingPayload } from '@/lib/pairing';
import { t } from '@/lib/i18n';
import { useHostsStore } from '@/store/hosts';
import { showToast } from '@/store/toast';
import { radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

type Mode = 'qr' | 'manual';

export default function PairScreen() {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [mode, setMode] = useState<Mode>('qr');
  const [permission, requestPermission] = useCameraPermissions();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);
  const [link, setLink] = useState('');
  const addHost = useHostsStore((state) => state.addHost);
  const ensureClientId = useHostsStore((state) => state.ensureClientId);

  const pair = useCallback(
    async (payload: PairingPayload) => {
      setBusy(true);
      setError(undefined);
      try {
        // iOS allows plain `ws://` only inside the local network, so a public relay
        // behind `ws://` would fail as a silent connection timeout. Say so instead.
        const blocked = relayTransportError(payload.relayUrl, Platform.OS);
        if (blocked) {
          setError(blocked);
          return;
        }
        // `clientId` travels with the pairing key so the Mac can register this phone and
        // answer with a device token; a Mac that knows no tokens simply ignores it. It
        // is read here rather than from the store, so a cold start or a deep link into
        // this screen can never pair without one.
        const clientId = await ensureClientId();
        const info = await probeHost({
          serverId: payload.serverId,
          relayUrl: payload.relayUrl,
          hostPublicKeyB64: payload.hostPublicKeyB64,
          pairingKey: payload.pairingKey,
          clientId,
        });
        const saved = await addHost(
          { ...payload, name: payload.name || info.hostName || payload.serverId },
          { hostId: info.hostId, appVersion: info.appVersion, deviceToken: info.deviceToken },
        );
        showToast(t('phone.pair.paired', { name: saved.name }), 'success');
        router.replace(`/host/${saved.id}`);
      } catch (caught) {
        setError(describeError(caught));
      } finally {
        setBusy(false);
      }
    },
    [addHost, ensureClientId],
  );

  const onScanned = useCallback(
    (value: string) => {
      if (busy) return;
      const parsed = parsePairingUrl(value);
      if (!parsed.ok) {
        setError(parsed.error);
        return;
      }
      void pair(parsed.value);
    },
    [busy, pair],
  );

  const onManualSubmit = useCallback(() => {
    const parsed = parsePairingUrl(link);
    if (!parsed.ok) {
      setError(parsed.error);
      return;
    }
    void pair(parsed.value);
  }, [link, pair]);

  return (
    <KeyboardAvoidingView
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
      style={styles.screen}
    >
      <View style={styles.modes}>
        <Chip
          label={t('phone.pair.mode.qr')}
          selected={mode === 'qr'}
          color={palette.accent}
          onPress={() => setMode('qr')}
        />
        <Chip
          label={t('phone.pair.mode.manual')}
          selected={mode === 'manual'}
          color={palette.accent}
          onPress={() => setMode('manual')}
        />
      </View>

      {error ? <ErrorBanner message={error} /> : null}

      {mode === 'qr' ? (
        <View style={styles.scannerWrap}>
          {!permission ? (
            <Text style={styles.hint}>{t('phone.pair.permissionChecking')}</Text>
          ) : !permission.granted ? (
            <Card style={styles.permissionCard}>
              <Text style={styles.hint}>{t('phone.pair.permissionNeeded')}</Text>
              <Button
                label={t('phone.pair.permissionAllow')}
                tone="primary"
                onPress={() => void requestPermission()}
              />
            </Card>
          ) : (
            <View style={styles.camera}>
              <CameraView
                style={StyleSheet.absoluteFill}
                facing="back"
                barcodeScannerSettings={{ barcodeTypes: ['qr'] }}
                onBarcodeScanned={({ data }) => onScanned(data)}
              />
              <View pointerEvents="none" style={styles.reticle} />
            </View>
          )}
          <Text style={styles.hint}>{t('phone.pair.qrHint')}</Text>
        </View>
      ) : (
        <ScrollView contentContainerStyle={styles.form} keyboardShouldPersistTaps="handled">
          <Field
            label={t('phone.pair.linkLabel')}
            value={link}
            onChange={setLink}
            placeholder="mightyclaude://pair?v=2&sid=…"
            autoCapitalize="none"
            multiline
          />
          <Text style={styles.hint}>{t('phone.pair.linkHint')}</Text>
          <Button
            label={t('phone.pair.connect')}
            tone="primary"
            busy={busy}
            onPress={onManualSubmit}
          />
        </ScrollView>
      )}
    </KeyboardAvoidingView>
  );
}

interface FieldProps {
  label: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  autoCapitalize?: 'none' | 'sentences';
  multiline?: boolean;
}

function Field({
  label,
  value,
  onChange,
  placeholder,
  autoCapitalize = 'sentences',
  multiline = false,
}: FieldProps) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.field}>
      <Text style={styles.fieldLabel}>{label}</Text>
      <TextInput
        value={value}
        onChangeText={onChange}
        placeholder={placeholder}
        placeholderTextColor={palette.textFaint}
        autoCapitalize={autoCapitalize}
        autoCorrect={false}
        multiline={multiline}
        style={[styles.input, multiline && styles.inputMultiline]}
      />
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    modes: { flexDirection: 'row', gap: spacing.sm, padding: spacing.lg },
    scannerWrap: { flex: 1, gap: spacing.lg, padding: spacing.lg },
    permissionCard: { gap: spacing.lg },
    camera: {
      backgroundColor: '#000',
      borderRadius: radius.lg,
      flex: 1,
      overflow: 'hidden',
    },
    reticle: {
      alignSelf: 'center',
      borderColor: palette.accent,
      borderRadius: radius.lg,
      borderWidth: 2,
      height: 220,
      marginTop: '30%',
      width: 220,
    },
    hint: { color: palette.textMuted, fontSize: 13, textAlign: 'center' },
    form: { gap: spacing.lg, padding: spacing.lg },
    field: { gap: spacing.xs },
    fieldLabel: { color: palette.textMuted, fontSize: 13 },
    input: {
      backgroundColor: palette.surface,
      borderColor: palette.border,
      borderRadius: radius.md,
      borderWidth: StyleSheet.hairlineWidth,
      color: palette.text,
      fontSize: 15,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.md,
    },
    inputMultiline: { minHeight: 96, textAlignVertical: 'top' },
  });
