import { useMemo } from 'react';
import { ScrollView, StyleSheet, Text, View } from 'react-native';
import type { RateLimit, StatusLine } from '@/api/types';
import { sanitiseRateLimits, sanitiseStatusLines } from '@/lib/status-line';
import { monoText, radius, spacing, useStyles, type Palette } from '@/theme';

/** `2026-09-18T04:00:00Z` → `04:00`; anything unparsable is simply not shown. */
function formatReset(value: string | undefined): string | undefined {
  if (!value) return undefined;
  const at = new Date(value);
  if (Number.isNaN(at.getTime())) return undefined;
  const hours = String(at.getHours()).padStart(2, '0');
  const minutes = String(at.getMinutes()).padStart(2, '0');
  return `${hours}:${minutes}`;
}

/**
 * The host's status line and rate-limit bars. Both payloads go through the sanitiser
 * first, so an unknown colour or a malformed entry is dropped rather than rendered.
 */
export function StatusLineView({
  statusLine,
  rateLimits,
}: {
  statusLine?: StatusLine;
  rateLimits?: RateLimit[];
}) {
  const styles = useStyles(makeStyles);
  const lines = useMemo(() => sanitiseStatusLines(statusLine), [statusLine]);
  const limits = useMemo(() => sanitiseRateLimits(rateLimits), [rateLimits]);

  if (lines.length === 0 && limits.length === 0) return null;

  return (
    <View style={styles.wrap}>
      {lines.length > 0 ? (
        <ScrollView horizontal showsHorizontalScrollIndicator={false}>
          <View>
            {lines.map((segments, lineIndex) => (
              <Text key={`line-${lineIndex}`} numberOfLines={1} style={styles.line}>
                {segments.map((segment, index) => (
                  <Text
                    key={`segment-${index}`}
                    style={[
                      segment.fg ? { color: segment.fg } : styles.segmentDefault,
                      segment.bold ? styles.bold : undefined,
                    ]}
                  >
                    {segment.text}
                  </Text>
                ))}
              </Text>
            ))}
          </View>
        </ScrollView>
      ) : null}

      {limits.map((limit, index) => (
        <RateLimitBar key={`${index}-${limit.label}`} limit={limit} />
      ))}
    </View>
  );
}

function RateLimitBar({ limit }: { limit: RateLimit }) {
  const styles = useStyles(makeStyles);
  const resetsAt = formatReset(limit.resetsAt);
  return (
    <View style={styles.limit}>
      <View style={styles.limitRow}>
        <Text numberOfLines={1} style={styles.limitLabel}>
          {limit.label}
        </Text>
        <Text style={styles.limitValue}>
          {Math.round(limit.usedPercent)}%{resetsAt ? ` · ${resetsAt} 초기화` : ''}
        </Text>
      </View>
      <View style={styles.limitTrack}>
        <View style={[styles.limitFill, { width: `${limit.usedPercent}%` }]} />
      </View>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    wrap: {
      backgroundColor: palette.surface,
      borderRadius: radius.sm,
      gap: spacing.xs,
      marginTop: spacing.xs,
      padding: spacing.sm,
    },
    line: { ...monoText, color: palette.textMuted },
    segmentDefault: { color: palette.textMuted },
    bold: { fontWeight: '700' },
    limit: { gap: 2 },
    limitRow: { flexDirection: 'row', gap: spacing.sm },
    limitLabel: { color: palette.textMuted, flex: 1, fontSize: 11 },
    limitValue: { color: palette.textFaint, fontSize: 11 },
    limitTrack: {
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.sm,
      height: 4,
      overflow: 'hidden',
    },
    limitFill: { backgroundColor: palette.accent, height: 4 },
  });
