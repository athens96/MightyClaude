import { StyleSheet, View } from 'react-native';
import type { Reachability } from '@/store/hosts';
import { radius, type Palette } from '@/theme';

export const reachabilityKeys: Record<Reachability, string> = {
  unknown: 'phone.hosts.reachability.unknown',
  checking: 'phone.hosts.reachability.checking',
  online: 'phone.hosts.reachability.online',
  unauthorized: 'phone.hosts.reachability.unauthorized',
  offline: 'phone.hosts.reachability.offline',
  'relay-offline': 'phone.hosts.reachability.relayOffline',
};

/** The word's colour: an ink that reads on a white card. */
export function reachabilityColor(palette: Palette, reachability: Reachability): string {
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

/** The dot's colour: the bold fill, with its soft tint as a halo. */
function dotColors(palette: Palette, reachability: Reachability): { fill: string; halo: string } {
  switch (reachability) {
    case 'online':
      return { fill: palette.done, halo: palette.doneSoft };
    case 'unauthorized':
      return { fill: palette.err, halo: palette.dangerSurface };
    case 'relay-offline':
      return { fill: palette.wait, halo: palette.waitSoft };
    case 'offline':
    case 'checking':
    case 'unknown':
      return { fill: palette.stop, halo: palette.stopSoft };
  }
}

/** The live dot in front of a host's name: green with a halo while the host answers. */
export function LiveDot({ palette, reachability }: { palette: Palette; reachability: Reachability }) {
  const colors = dotColors(palette, reachability);
  return (
    <View style={[styles.halo, { backgroundColor: colors.halo }]}>
      <View style={[styles.dot, { backgroundColor: colors.fill }]} />
    </View>
  );
}

const styles = StyleSheet.create({
  halo: { alignItems: 'center', borderRadius: radius.round, height: 14, justifyContent: 'center', width: 14 },
  dot: { borderRadius: radius.round, height: 8, width: 8 },
});
