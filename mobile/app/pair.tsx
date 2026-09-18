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
import { parsePairingUrl, type PairingPayload } from '@/lib/pairing';
import { useHostsStore } from '@/store/hosts';
import { showToast } from '@/store/toast';
import { colors, radius, spacing } from '@/theme';

type Mode = 'qr' | 'manual';

export default function PairScreen() {
  const [mode, setMode] = useState<Mode>('qr');
  const [permission, requestPermission] = useCameraPermissions();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);
  const [link, setLink] = useState('');
  const addHost = useHostsStore((state) => state.addHost);

  const pair = useCallback(
    async (payload: PairingPayload) => {
      setBusy(true);
      setError(undefined);
      try {
        const info = await probeHost({
          serverId: payload.serverId,
          relayUrl: payload.relayUrl,
          hostPublicKeyB64: payload.hostPublicKeyB64,
          pairingKey: payload.pairingKey,
        });
        const saved = await addHost(
          { ...payload, name: payload.name || info.hostName || payload.serverId },
          { hostId: info.hostId, appVersion: info.appVersion },
        );
        showToast(`${saved.name} 페어링 완료`, 'success');
        router.replace(`/host/${saved.id}`);
      } catch (caught) {
        setError(describeError(caught));
      } finally {
        setBusy(false);
      }
    },
    [addHost],
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
        <Chip label="QR 스캔" selected={mode === 'qr'} color={colors.accent} onPress={() => setMode('qr')} />
        <Chip
          label="링크 붙여넣기"
          selected={mode === 'manual'}
          color={colors.accent}
          onPress={() => setMode('manual')}
        />
      </View>

      {error ? <ErrorBanner message={error} /> : null}

      {mode === 'qr' ? (
        <View style={styles.scannerWrap}>
          {!permission ? (
            <Text style={styles.hint}>카메라 권한을 확인하는 중…</Text>
          ) : !permission.granted ? (
            <Card style={styles.permissionCard}>
              <Text style={styles.hint}>
                페어링 QR 코드를 읽으려면 카메라 권한이 필요합니다.
              </Text>
              <Button
                label="카메라 권한 허용"
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
          <Text style={styles.hint}>
            Mac의 설정 → 모바일 리모트에서 QR 코드를 띄우고 화면에 맞추세요.
          </Text>
        </View>
      ) : (
        <ScrollView contentContainerStyle={styles.form} keyboardShouldPersistTaps="handled">
          <Field
            label="페어링 링크"
            value={link}
            onChange={setLink}
            placeholder="mightyclaude://pair?v=2&sid=…"
            autoCapitalize="none"
            multiline
          />
          <Text style={styles.hint}>
            Mac의 설정 → 모바일 리모트에서 페어링 링크를 복사해 붙여넣으세요.
          </Text>
          <Button label="연결하기" tone="primary" busy={busy} onPress={onManualSubmit} />
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
  return (
    <View style={styles.field}>
      <Text style={styles.fieldLabel}>{label}</Text>
      <TextInput
        value={value}
        onChangeText={onChange}
        placeholder={placeholder}
        placeholderTextColor={colors.textFaint}
        autoCapitalize={autoCapitalize}
        autoCorrect={false}
        multiline={multiline}
        style={[styles.input, multiline && styles.inputMultiline]}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  screen: { backgroundColor: colors.background, flex: 1 },
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
    borderColor: colors.accent,
    borderRadius: radius.lg,
    borderWidth: 2,
    height: 220,
    marginTop: '30%',
    width: 220,
  },
  hint: { color: colors.textMuted, fontSize: 13, textAlign: 'center' },
  form: { gap: spacing.lg, padding: spacing.lg },
  field: { gap: spacing.xs },
  fieldLabel: { color: colors.textMuted, fontSize: 13 },
  input: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: radius.md,
    borderWidth: StyleSheet.hairlineWidth,
    color: colors.text,
    fontSize: 15,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.md,
  },
  inputMultiline: { minHeight: 96, textAlignVertical: 'top' },
});
