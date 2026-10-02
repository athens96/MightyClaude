import { Platform } from 'react-native';
import { router } from 'expo-router';
import { Button } from '@/components/ui';
import { useCapabilities } from '@/hooks/use-capabilities';
import { t } from '@/lib/i18n';
import { screenShareVisibility } from '@/lib/screen-share/availability';
import { useHostClient } from '@/store/live';

/**
 * The way into 화면 보기, and nothing at all on a phone or a Mac that cannot do it: iOS
 * is out of scope for this beta, and a Mac older than the feature never advertises
 * `screenShare`. In both cases the host screen looks exactly as it did before.
 */
export function ScreenShareEntry({ hostId, hostName }: { hostId: string; hostName: string }) {
  const client = useHostClient(hostId);
  const capabilities = useCapabilities(hostId, client);
  const visibility = screenShareVisibility({ platform: Platform.OS, capabilities });
  if (!visibility.visible) return null;
  return (
    <Button
      label={t('phone.screenShare.open')}
      accessibilityLabel={t('phone.screenShare.openLabel', { name: hostName })}
      compact
      onPress={() => router.push(`/host/${hostId}/screen`)}
    />
  );
}
