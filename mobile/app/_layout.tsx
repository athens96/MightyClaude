import { useEffect } from 'react';
import { StyleSheet, View } from 'react-native';
import { DarkTheme, Stack, ThemeProvider } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { ToastHost } from '@/components/toast-host';
import { installCryptoPolyfill } from '@/api/relay/random';
import { useHostsStore } from '@/store/hosts';
import { colors } from '@/theme';

// `@noble/*` reads `globalThis.crypto.getRandomValues`, which React Native lacks.
installCryptoPolyfill();

const navigationTheme: typeof DarkTheme = {
  ...DarkTheme,
  dark: true,
  colors: {
    ...DarkTheme.colors,
    primary: colors.accent,
    background: colors.background,
    card: colors.surface,
    text: colors.text,
    border: colors.border,
    notification: colors.accent,
  },
};

export default function RootLayout() {
  const load = useHostsStore((state) => state.load);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <SafeAreaProvider>
      <ThemeProvider value={navigationTheme}>
        <View style={styles.root}>
          <StatusBar style="light" />
          <Stack
            screenOptions={{
              headerStyle: { backgroundColor: colors.background },
              headerTintColor: colors.text,
              headerTitleStyle: { color: colors.text, fontSize: 16 },
              contentStyle: { backgroundColor: colors.background },
            }}
          >
            <Stack.Screen name="index" options={{ title: '호스트' }} />
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

const styles = StyleSheet.create({
  root: { backgroundColor: colors.background, flex: 1 },
});
