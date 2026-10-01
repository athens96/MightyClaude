import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  FlatList,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Stack, router, useLocalSearchParams, useNavigation } from 'expo-router';
import { usePreventRemove } from 'expo-router/react-navigation';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { isAbortError } from '@/api/client';
import type { FileEntry, FileListing } from '@/api/types';
import { Button, EmptyState, ErrorBanner } from '@/components/ui';
import {
  backTarget,
  breadcrumbs,
  describeFileError,
  displayName,
  filterEntries,
  formatModified,
  isFolder,
  isSymlink,
  parentPath,
} from '@/lib/files';
import { t } from '@/lib/i18n';
import { formatBytes } from '@/lib/uploads';
import { useHostClient, useHostState } from '@/store/live';
import { radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

/**
 * One workspace's folders on the paired Mac, read-only: the Mac files pane's tree as a
 * phone list, one folder at a time. The folder is read again on every visit (a listing
 * read earlier shows at once meanwhile) and on pull to refresh.
 */
export default function FilesScreen() {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const insets = useSafeAreaInsets();
  const navigation = useNavigation();
  const params = useLocalSearchParams<{ hostId: string; workspaceId: string; path?: string }>();
  const { hostId, workspaceId } = params;
  const client = useHostClient(hostId);
  const workspace = useHostState(hostId)?.workspaces.find((entry) => entry.id === workspaceId);
  const rootLabel = workspace?.name ?? t('phone.files.title');

  const [path, setPath] = useState(params.path ?? '');
  const [query, setQuery] = useState('');
  const [listing, setListing] = useState<FileListing | undefined>(undefined);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);
  const [reload, setReload] = useState(0);
  const listings = useRef(new Map<string, FileListing>());
  const failed = useRef(false);

  useEffect(() => {
    if (!client || !workspaceId) return undefined;
    const controller = new AbortController();
    setListing(listings.current.get(path));
    setLoading(true);
    setError(undefined);
    failed.current = false;
    client
      .listFiles(workspaceId, path, controller.signal)
      .then((data) => {
        if (controller.signal.aborted) return;
        listings.current.set(path, data);
        setListing(data);
      })
      .catch((reason: unknown) => {
        if (controller.signal.aborted || isAbortError(reason)) return;
        failed.current = true;
        setError(describeFileError(reason));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [client, path, reload, workspaceId]);

  // A folder that failed while the tunnel was down is read again once it is back.
  useEffect(
    () =>
      client?.onStateChange((state) => {
        if (state === 'ready' && failed.current) setReload((count) => count + 1);
      }),
    [client],
  );

  const open = useCallback((next: string) => {
    setQuery('');
    setPath(next);
  }, []);

  // Back walks up the folders before it leaves the screen, the same on both
  // platforms: the header's back button, the iOS edge swipe and Android's back button.
  // Any other navigation (a reset, a jump elsewhere) goes through untouched.
  usePreventRemove(path !== '', ({ data }) => {
    const target = backTarget(path, data.action);
    if (target === undefined) navigation.dispatch(data.action);
    else open(target);
  });

  const entries = useMemo(
    () => (listing && listing.path === path ? filterEntries(listing.entries, query) : []),
    [listing, path, query],
  );
  const crumbs = useMemo(() => breadcrumbs(path, rootLabel), [path, rootLabel]);

  const onPress = (entry: FileEntry) => {
    if (isFolder(entry.kind)) {
      open(entry.relativePath);
      return;
    }
    router.push({
      pathname: '/host/[hostId]/workspace/[workspaceId]/file',
      params: { hostId: hostId ?? '', workspaceId: workspaceId ?? '', path: entry.relativePath },
    });
  };

  const renderEntry = ({ item }: { item: FileEntry }) => {
    const folder = isFolder(item.kind);
    const name = displayName(item.name);
    const size = folder || item.size === undefined ? '' : formatBytes(item.size);
    const modified = formatModified(item.modified);
    const label = folder
      ? t('phone.files.folderLabel', { name })
      : t('phone.files.fileLabel', { name, size });
    return (
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={isSymlink(item.kind) ? `${label}, ${t('phone.files.symlink')}` : label}
        accessibilityHint={item.noise ? t('phone.files.noiseHint') : undefined}
        onPress={() => onPress(item)}
        style={({ pressed }) => [styles.row, item.noise && styles.noise, pressed && styles.pressed]}
      >
        <Text style={[styles.glyph, folder && styles.folderGlyph]}>{folder ? '▸' : '·'}</Text>
        <View style={styles.rowText}>
          <Text numberOfLines={1} style={[styles.name, folder && styles.folderName]}>
            {name}
            {isSymlink(item.kind) ? <Text style={styles.link}> ↗</Text> : null}
          </Text>
          {modified ? <Text style={styles.meta}>{modified}</Text> : null}
        </View>
        {size ? <Text style={styles.size}>{size}</Text> : null}
        {folder ? <Text style={styles.chevron}>›</Text> : null}
      </Pressable>
    );
  };

  const shown = listing && listing.path === path;
  const empty = !shown
    ? loading
      ? t('phone.files.loading')
      : ''
    : query.trim()
      ? t('phone.files.noMatches')
      : t('phone.files.empty');

  return (
    <View style={styles.screen}>
      <Stack.Screen options={{ title: rootLabel }} />

      <ScrollView
        horizontal
        showsHorizontalScrollIndicator={false}
        style={styles.crumbBar}
        contentContainerStyle={styles.crumbs}
      >
        {crumbs.map((crumb, index) => {
          const last = index === crumbs.length - 1;
          return (
            <View key={crumb.path} style={styles.crumbItem}>
              {index > 0 ? <Text style={styles.crumbSeparator}>/</Text> : null}
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={t('phone.files.breadcrumbLabel', { name: crumb.label })}
                accessibilityState={{ selected: last }}
                disabled={last}
                onPress={() => open(crumb.path)}
              >
                <Text numberOfLines={1} style={[styles.crumb, last && styles.crumbCurrent]}>
                  {crumb.label}
                </Text>
              </Pressable>
            </View>
          );
        })}
      </ScrollView>

      <TextInput
        value={query}
        onChangeText={setQuery}
        placeholder={t('phone.files.filter')}
        placeholderTextColor={palette.textFaint}
        accessibilityLabel={t('phone.files.filterLabel')}
        autoCapitalize="none"
        autoCorrect={false}
        clearButtonMode="while-editing"
        returnKeyType="search"
        style={styles.filter}
      />

      {error ? (
        <View style={styles.errorRow}>
          <ErrorBanner message={error} />
          <Button
            label={t('phone.files.retry')}
            compact
            onPress={() => setReload((count) => count + 1)}
            style={styles.retry}
          />
        </View>
      ) : null}

      <FlatList
        data={entries}
        keyExtractor={(entry) => entry.relativePath}
        renderItem={renderEntry}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode="on-drag"
        contentContainerStyle={[styles.list, { paddingBottom: insets.bottom + spacing.xl }]}
        ListHeaderComponent={
          path ? (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t('phone.files.up')}
              onPress={() => open(parentPath(path))}
              style={({ pressed }) => [styles.row, pressed && styles.pressed]}
            >
              <Text style={styles.glyph}>‹</Text>
              <Text style={styles.upLabel}>{t('phone.files.up')}</Text>
            </Pressable>
          ) : null
        }
        ListEmptyComponent={empty ? <EmptyState title={empty} /> : null}
        ListFooterComponent={
          shown && listing.truncated ? (
            <Text style={styles.truncated}>
              {t('phone.files.truncated', { count: listing.entries.length.toLocaleString() })}
            </Text>
          ) : null
        }
        refreshControl={
          <RefreshControl
            refreshing={loading && shown === true}
            onRefresh={() => setReload((count) => count + 1)}
            tintColor={palette.accent}
          />
        }
      />
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    crumbBar: { flexGrow: 0 },
    crumbs: { alignItems: 'center', paddingHorizontal: spacing.lg, paddingVertical: spacing.sm },
    crumbItem: { alignItems: 'center', flexDirection: 'row' },
    crumbSeparator: { color: palette.textFaint, fontSize: 13, paddingHorizontal: spacing.xs },
    crumb: { color: palette.accent, fontSize: 13, fontWeight: '600', maxWidth: 200, paddingVertical: spacing.xs },
    crumbCurrent: { color: palette.text, fontWeight: '700' },
    filter: {
      backgroundColor: palette.surface,
      borderColor: palette.border,
      borderRadius: radius.round,
      borderWidth: StyleSheet.hairlineWidth,
      color: palette.text,
      fontSize: 15,
      marginHorizontal: spacing.lg,
      minHeight: 40,
      paddingHorizontal: spacing.md,
    },
    errorRow: { gap: spacing.sm },
    retry: { alignSelf: 'flex-start', marginHorizontal: spacing.lg },
    list: { paddingBottom: spacing.lg, paddingHorizontal: spacing.lg, paddingTop: spacing.sm },
    // Entries are white rows on the grey page, a little apart, as the session cards are.
    row: {
      alignItems: 'center',
      backgroundColor: palette.surface,
      borderRadius: radius.lg - 2,
      flexDirection: 'row',
      gap: spacing.md,
      marginBottom: 6,
      minHeight: 48,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
    },
    noise: { opacity: 0.45 },
    pressed: { opacity: 0.6 },
    glyph: { color: palette.textFaint, fontSize: 16, textAlign: 'center', width: 16 },
    folderGlyph: { color: palette.textMuted },
    rowText: { flex: 1, gap: 2 },
    name: { color: palette.text, fontSize: 15 },
    folderName: { fontWeight: '700' },
    link: { color: palette.textMuted },
    meta: { color: palette.textFaint, fontSize: 12 },
    size: { color: palette.textMuted, fontSize: 12 },
    chevron: { color: palette.textFaint, fontSize: 18 },
    upLabel: { color: palette.textMuted, flex: 1, fontSize: 15 },
    truncated: { color: palette.warning, fontSize: 12, paddingVertical: spacing.sm, textAlign: 'center' },
  });
