import { useEffect, useMemo } from 'react';
import { StyleSheet, View } from 'react-native';
import { DarkTheme, DefaultTheme, Stack, ThemeProvider } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { ToastHost } from '@/components/toast-host';
import { installCryptoPolyfill } from '@/api/relay/random';
import { useHostsStore } from '@/store/hosts';
import { t } from '@/lib/i18n';
import { darkPalette, useStyles, usePalette, type Palette } from '@/theme';

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
        card: palette.surface,
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
        <View style={styles.root}>
          <StatusBar style={dark ? 'light' : 'dark'} />
          <Stack
            screenOptions={{
              headerStyle: { backgroundColor: palette.background },
              headerTintColor: palette.text,
              headerTitleStyle: { color: palette.text, fontSize: 16 },
              contentStyle: { backgroundColor: palette.background },
            }}
          >
            <Stack.Screen name="index" options={{ title: '호스트' }} />
            <Stack.Screen name="connect" options={{ title: t('phone.connect.title'), presentation: 'modal' }} />
            <Stack.Screen name="pair" options={{ title: '호스트 추가', presentation: 'modal' }} />
            <Stack.Screen name="host/[hostId]/index" options={{ title: '작업 공간' }} />
            <Stack.Screen name="host/[hostId]/session/[sessionId]" options={{ title: '세션' }} />
          </Stack>
          <ToastHost />
        </View>
      </ThemeProvider>
    </SafeAreaProvider>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    root: { backgroundColor: palette.background, flex: 1 },
  });
