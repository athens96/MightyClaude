import { useCallback, useEffect, useState } from 'react';
import { Alert, FlatList, Pressable, RefreshControl, StyleSheet, Text, View } from 'react-native';
import { router, useFocusEffect } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { appForeground } from '@/api/relay/foreground';
import { Badge, Button, Card, EmptyState } from '@/components/ui';
import { hostAddress, useHostsStore, type PairedHost, type Reachability } from '@/store/hosts';
import { useLiveStore } from '@/store/live';
import { countAttention } from '@/lib/merge';
import { t } from '@/lib/i18n';
import { needsOnboarding } from '@/lib/onboarding';
import { spacing, useStyles, usePalette, type Palette } from '@/theme';

const reachabilityKeys: Record<Reachability, string> = {
  unknown: 'phone.hosts.reachability.unknown',
  checking: 'phone.hosts.reachability.checking',
  online: 'phone.hosts.reachability.online',
  unauthorized: 'phone.hosts.reachability.unauthorized',
  offline: 'phone.hosts.reachability.offline',
  'relay-offline': 'phone.hosts.reachability.relayOffline',
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
    Alert.alert(t('phone.hosts.remove.title'), t('phone.hosts.remove.message', { name: host.name }), [
      { text: t('phone.hosts.remove.cancel'), style: 'cancel' },
      {
        text: t('phone.hosts.remove.confirm'),
        style: 'destructive',
        onPress: () => void removeHost(host.id),
      },
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
          <Text style={[styles.status, { color }]}>{t(reachabilityKeys[status])}</Text>
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

  useEffect(() => {
    if (loaded && needsOnboarding(hosts.length)) {
      router.replace('/connect');
    }
  }, [loaded, hosts.length]);

  const onRefresh = useCallback(async () => {
    setRefreshing(true);
    await refreshAll();
    setRefreshing(false);
  }, [refreshAll]);

  // What the list says about each host was true when the app went away; ask again
  // when it comes back, as a pull would.
  useFocusEffect(
    useCallback(() => appForeground.subscribe(() => void refreshAll()), [refreshAll]),
  );

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
              title={t('phone.hosts.empty.title')}
              description={t('phone.hosts.empty.description')}
            />
          ) : null
        }
      />
      <View style={[styles.footer, { paddingBottom: insets.bottom + spacing.md }]}>
        <Button
          label={t('phone.hosts.addHost')}
          tone="primary"
          onPress={() => router.push('/pair')}
        />
        <Button
          label={t('phone.hosts.guide')}
          tone="ghost"
          onPress={() => router.push('/connect')}
        />
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
