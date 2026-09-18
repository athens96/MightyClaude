import { useCallback, useState } from 'react';
import { Alert, FlatList, Pressable, RefreshControl, StyleSheet, Text, View } from 'react-native';
import { router } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { Badge, Button, Card, EmptyState } from '@/components/ui';
import { hostAddress, useHostsStore, type PairedHost, type Reachability } from '@/store/hosts';
import { useLiveStore } from '@/store/live';
import { countAttention } from '@/lib/merge';
import { spacing, useStyles, usePalette, type Palette } from '@/theme';

const reachabilityLabels: Record<Reachability, string> = {
  unknown: '확인 전',
  checking: '확인 중…',
  online: '연결됨',
  unauthorized: '재페어링 필요',
  offline: '호스트 오프라인',
  'relay-offline': '릴레이 연결 안 됨',
};

function reachabilityColor(palette: Palette, reachability: Reachability): string {
  switch (reachability) {
    case 'online':
      return palette.success;
    case 'unauthorized':
      return palette.danger;
    case 'offline':
      return palette.grey;
    case 'relay-offline':
      return palette.warning;
    case 'checking':
      return palette.textMuted;
    case 'unknown':
      return palette.textFaint;
  }
}

function HostRow({ host }: { host: PairedHost }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const status = useHostsStore((state) => state.status[host.id]?.reachability ?? 'unknown');
  const attention = useLiveStore((store) => {
    const state = store.states[host.id];
    return state ? countAttention(state) : 0;
  });
  const removeHost = useHostsStore((state) => state.removeHost);
  const color = reachabilityColor(palette, status);

  const confirmRemove = useCallback(() => {
    Alert.alert('호스트 삭제', `${host.name} 페어링을 삭제할까요?`, [
      { text: '취소', style: 'cancel' },
      { text: '삭제', style: 'destructive', onPress: () => void removeHost(host.id) },
    ]);
  }, [host.id, host.name, removeHost]);

  return (
    <Pressable
      accessibilityRole="button"
      onPress={() => router.push(`/host/${host.id}`)}
      onLongPress={confirmRemove}
      style={({ pressed }) => [pressed && styles.pressed]}
    >
      <Card>
        <View style={styles.rowTop}>
          <Text numberOfLines={1} style={styles.hostName}>
            {host.name}
          </Text>
          <Badge count={attention} />
        </View>
        <Text style={styles.address}>{hostAddress(host)}</Text>
        <View style={styles.rowBottom}>
          <View style={[styles.dot, { backgroundColor: color }]} />
          <Text style={[styles.status, { color }]}>{reachabilityLabels[status]}</Text>
          {host.appVersion ? <Text style={styles.meta}>· v{host.appVersion}</Text> : null}
        </View>
      </Card>
    </Pressable>
  );
}

export default function HostsScreen() {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const hosts = useHostsStore((state) => state.hosts);
  const loaded = useHostsStore((state) => state.loaded);
  const refreshAll = useHostsStore((state) => state.refreshAll);
  const [refreshing, setRefreshing] = useState(false);
  const insets = useSafeAreaInsets();

  const onRefresh = useCallback(async () => {
    setRefreshing(true);
    await refreshAll();
    setRefreshing(false);
  }, [refreshAll]);

  return (
    <View style={styles.screen}>
      <FlatList
        data={hosts}
        keyExtractor={(host) => host.id}
        renderItem={({ item }) => <HostRow host={item} />}
        contentContainerStyle={[styles.list, { paddingBottom: insets.bottom + 96 }]}
        ItemSeparatorComponent={() => <View style={styles.separator} />}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => void onRefresh()}
            tintColor={palette.accent}
          />
        }
        ListEmptyComponent={
          loaded ? (
            <EmptyState
              title="페어링된 호스트가 없습니다"
              description="데스크톱 MightyClaude에서 QR 코드를 띄우고 아래 버튼으로 추가하세요."
            />
          ) : null
        }
      />
      <View style={[styles.footer, { paddingBottom: insets.bottom + spacing.md }]}>
        <Button label="+ 호스트 추가" tone="primary" onPress={() => router.push('/pair')} />
      </View>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    list: { padding: spacing.lg },
    separator: { height: spacing.md },
    pressed: { opacity: 0.7 },
    rowTop: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    hostName: { color: palette.text, flex: 1, fontSize: 17, fontWeight: '700' },
    address: { color: palette.textMuted, fontSize: 13, marginTop: 2 },
    rowBottom: {
      alignItems: 'center',
      flexDirection: 'row',
      gap: spacing.xs,
      marginTop: spacing.sm,
    },
    dot: { borderRadius: 4, height: 8, width: 8 },
    status: { fontSize: 12, fontWeight: '600' },
    meta: { color: palette.textFaint, fontSize: 12 },
    footer: {
      backgroundColor: palette.background,
      borderTopColor: palette.border,
      borderTopWidth: StyleSheet.hairlineWidth,
      bottom: 0,
      left: 0,
      padding: spacing.lg,
      position: 'absolute',
      right: 0,
    },
  });
