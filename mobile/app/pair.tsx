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
import { createClient, describeError } from '@/api/client';
import { DEFAULT_PORT, PROTOCOL_VERSION } from '@/api/types';
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
  const [host, setHost] = useState('');
  const [port, setPort] = useState(String(DEFAULT_PORT));
  const [key, setKey] = useState('');
  const [name, setName] = useState('');
  const addHost = useHostsStore((state) => state.addHost);

  const pair = useCallback(
    async (payload: PairingPayload) => {
      setBusy(true);
      setError(undefined);
      try {
        const info = await createClient({
          host: payload.host,
          port: payload.port,
          key: payload.key,
        }).info();
        if (info.protocol !== PROTOCOL_VERSION) {
          throw new Error(`호스트 프로토콜 버전이 다릅니다 (${info.protocol}).`);
        }
        const saved = await addHost(
          { ...payload, name: payload.name || info.hostName },
          { hostId: info.hostId, appVersion: info.appVersion, platform: info.platform },
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
    const parsedPort = Number(port.trim());
    if (!host.trim()) {
      setError('호스트 주소를 입력하세요.');
      return;
    }
    if (!Number.isInteger(parsedPort) || parsedPort < 1 || parsedPort > 65535) {
      setError('포트 번호가 올바르지 않습니다.');
      return;
    }
    if (!key.trim()) {
      setError('페어링 키를 입력하세요.');
      return;
    }
    void pair({
      host: host.trim(),
      port: parsedPort,
      key: key.trim(),
      name: name.trim() || host.trim(),
    });
  }, [host, key, name, pair, port]);

  return (
    <KeyboardAvoidingView
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
      style={styles.screen}
    >
      <View style={styles.modes}>
        <Chip label="QR 스캔" selected={mode === 'qr'} color={colors.accent} onPress={() => setMode('qr')} />
        <Chip
          label="직접 입력"
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
            데스크톱 MightyClaude의 &ldquo;모바일 연결&rdquo; QR 코드를 화면에 맞추세요.
          </Text>
        </View>
      ) : (
        <ScrollView contentContainerStyle={styles.form} keyboardShouldPersistTaps="handled">
          <Field label="호스트 (Tailscale IP)" value={host} onChange={setHost} placeholder="100.x.y.z" autoCapitalize="none" />
          <Field label="포트" value={port} onChange={setPort} placeholder={String(DEFAULT_PORT)} keyboardType="number-pad" />
          <Field label="페어링 키" value={key} onChange={setKey} placeholder="호스트에 표시된 키" autoCapitalize="none" secure />
          <Field label="이름 (선택)" value={name} onChange={setName} placeholder="내 맥북" />
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
  keyboardType?: 'default' | 'number-pad';
  autoCapitalize?: 'none' | 'sentences';
  secure?: boolean;
}

function Field({
  label,
  value,
  onChange,
  placeholder,
  keyboardType = 'default',
  autoCapitalize = 'sentences',
  secure = false,
}: FieldProps) {
  return (
    <View style={styles.field}>
      <Text style={styles.fieldLabel}>{label}</Text>
      <TextInput
        value={value}
        onChangeText={onChange}
        placeholder={placeholder}
        placeholderTextColor={colors.textFaint}
        keyboardType={keyboardType}
        autoCapitalize={autoCapitalize}
        autoCorrect={false}
        secureTextEntry={secure}
        style={styles.input}
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
});
