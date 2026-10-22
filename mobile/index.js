// The JS entry. React Native's own error handler is kept first, then background push and
// notification handling is registered before the app mounts (docs/mobile-remote.md "푸시 알림").
import './src/lib/push/default-error-handler';
import './src/lib/push/background';
import 'expo-router/entry';
