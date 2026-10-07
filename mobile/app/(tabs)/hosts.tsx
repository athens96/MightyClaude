import { useCallback, useState } from 'react';
import { Alert, FlatList, Linking, Pressable, RefreshControl, StyleSheet, Text, View } from 'react-native';
import { router } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { LiveDot, reachabilityColor, reachabilityKeys } from '@/components/host-status';
import { Badge, Button, EmptyState, ScreenTitle } from '@/components/ui';
import { hostAddress, useHostsStore, type PairedHost } from '@/store/hosts';
import { useLiveStore } from '@/store/live';
import { countAttention } from '@/lib/merge';
import { helpSiteUrl } from '@/lib/help-site';
import { t } from '@/lib/i18n';
import {
  cardShadow,
  headingFontFamily,
  monoFontFamily,
  radius,
  spacing,
  useStyles,
  usePalette,
  type Palette,
} from '@/theme';

/** The live dot's 14pt halo plus the row's gap: where the host name, and the lines under it, begin. */
const NAME_INSET = 14 + spacing.sm;

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
      style={({ pressed }) => [styles.row, pressed && styles.pressed]}
    >
      <View style={styles.rowTop}>
        <LiveDot palette={palette} reachability={status} />
        <Text numberOfLines={1} style={styles.hostName}>
          {host.name}
        </Text>
        <Badge count={attention} />
      </View>
      <Text numberOfLines={1} style={styles.address}>
        {hostAddress(host)}
      </Text>
      <View style={styles.rowBottom}>
        <Text style={[styles.status, { color }]}>{t(reachabilityKeys[status])}</Text>
        {host.appVersion ? <Text style={styles.meta}>· v{host.appVersion}</Text> : null}
      </View>
    </Pressable>
  );
}

/** "호스트": the paired Macs as cards, with pairing, the connection guide and the user guide under them. */
export default function HostsTab() {
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
        contentContainerStyle={[
          styles.list,
          { paddingTop: insets.top + spacing.md, paddingBottom: spacing.xl },
        ]}
        ItemSeparatorComponent={() => <View style={styles.separator} />}
        ListHeaderComponent={<ScreenTitle title={t('phone.tabs.hosts')} style={styles.title} />}
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
        ListFooterComponent={
          <View style={styles.footer}>
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
            <Button
              label={t('phone.hosts.help')}
              tone="ghost"
              onPress={() => void Linking.openURL(helpSiteUrl())}
            />
          </View>
        }
      />
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    list: { paddingHorizontal: spacing.lg },
    title: { marginBottom: spacing.md, paddingHorizontal: spacing.xs },
    separator: { height: spacing.sm },
    row: {
      ...cardShadow,
      backgroundColor: palette.surface,
      borderRadius: radius.card,
      gap: spacing.xs,
      paddingHorizontal: spacing.lg,
      paddingVertical: spacing.lg,
    },
    pressed: { opacity: 0.85, transform: [{ scale: 0.99 }] },
    rowTop: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    hostName: {
      color: palette.text,
      flex: 1,
      fontFamily: headingFontFamily,
      fontSize: 17,
      fontWeight: '700',
    },
    address: { color: palette.textMuted, fontFamily: monoFontFamily, fontSize: 12, marginLeft: NAME_INSET },
    rowBottom: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm, marginLeft: NAME_INSET, marginTop: spacing.xs },
    status: { fontSize: 12, fontWeight: '700' },
    meta: { color: palette.textFaint, fontSize: 12 },
    footer: { gap: spacing.xs, paddingTop: spacing.lg },
  });
