import { useEffect, useMemo } from 'react';
import { StyleSheet } from 'react-native';
import { DarkTheme, DefaultTheme, Stack, ThemeProvider } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { GestureHandlerRootView } from 'react-native-gesture-handler';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { ToastHost } from '@/components/toast-host';
import { installCryptoPolyfill } from '@/api/relay/random';
import { useHostsStore } from '@/store/hosts';
import { t } from '@/lib/i18n';
import { darkPalette, serifFontFamily, useStyles, usePalette, type Palette } from '@/theme';

// `@noble/*` reads `globalThis.crypto.getRandomValues`, which React Native lacks.
installCryptoPolyfill();

export default function RootLayout() {
  const load = useHostsStore((state) => state.load);
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const dark = palette === darkPalette;

  // Navigation carries its own colours, so it follows the system scheme with the app.
  const navigationTheme = useMemo(() => {
    const base = dark ? DarkTheme : DefaultTheme;
    return {
      ...base,
      dark,
      colors: {
        ...base.colors,
        primary: palette.accent,
        background: palette.background,
        card: palette.background,
        text: palette.text,
        border: palette.border,
        notification: palette.accent,
      },
    };
  }, [dark, palette]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <SafeAreaProvider>
      <ThemeProvider value={navigationTheme}>
        {/* Gestures that run beside native scrolling (the chat's pull-to-refresh) need this root. */}
        <GestureHandlerRootView style={styles.root}>
          <StatusBar style={dark ? 'light' : 'dark'} />
          <Stack
            screenOptions={{
              // Paper all the way up: the header is the page itself, with no rule under it,
              // and its title is set in the serif.
              headerStyle: { backgroundColor: palette.background },
              headerShadowVisible: false,
              headerTintColor: palette.text,
              headerTitleStyle: {
                color: palette.text,
                fontFamily: serifFontFamily,
                fontSize: 18,
                fontWeight: '400',
              },
              contentStyle: { backgroundColor: palette.background },
            }}
          >
            <Stack.Screen name="index" options={{ title: '호스트' }} />
            <Stack.Screen name="connect" options={{ title: t('phone.connect.title'), presentation: 'modal' }} />
            <Stack.Screen name="pair" options={{ title: '호스트 추가', presentation: 'modal' }} />
            <Stack.Screen name="host/[hostId]/index" options={{ title: '작업 공간' }} />
            <Stack.Screen name="host/[hostId]/session/[sessionId]" options={{ title: '세션' }} />
            <Stack.Screen
              name="host/[hostId]/workspace/[workspaceId]/files"
              options={{ title: t('phone.files.title') }}
            />
            <Stack.Screen
              name="host/[hostId]/workspace/[workspaceId]/file"
              options={{ title: t('phone.files.title') }}
            />
          </Stack>
          <ToastHost />
        </GestureHandlerRootView>
      </ThemeProvider>
    </SafeAreaProvider>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    root: { backgroundColor: palette.background, flex: 1 },
  });
