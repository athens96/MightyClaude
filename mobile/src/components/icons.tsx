import type { ColorValue } from 'react-native';
import Svg, { Path, Rect } from 'react-native-svg';

/**
 * The few line glyphs concept D draws: the four tab marks, the composer's plus, send,
 * steer (a bolt), queue (lines with a plus) and stop, the next-action arrow, and the remote
 * screen's full-screen expand and collapse.
 * 24×24 strokes, round caps, coloured by the caller.
 * Decorative: whatever carries them holds the accessible label.
 */
export type IconName =
  | 'dashboard'
  | 'sessions'
  | 'alerts'
  | 'hosts'
  | 'plus'
  | 'send'
  | 'steer'
  | 'queue'
  | 'stop'
  | 'arrow'
  | 'chevronDown'
  | 'terminal'
  | 'globe'
  | 'expand'
  | 'collapse';

export function Icon({
  name,
  color,
  size = 24,
  strokeWidth = 2,
}: {
  name: IconName;
  color: ColorValue;
  size?: number;
  strokeWidth?: number;
}) {
  const stroke = {
    fill: 'none',
    stroke: color,
    strokeLinecap: 'round' as const,
    strokeLinejoin: 'round' as const,
    strokeWidth,
  };
  return (
    <Svg
      accessibilityElementsHidden
      height={size}
      importantForAccessibility="no-hide-descendants"
      viewBox="0 0 24 24"
      width={size}
    >
      {name === 'dashboard' ? (
        <>
          <Rect {...stroke} height={9} rx={2} width={7} x={3} y={3} />
          <Rect {...stroke} height={5} rx={2} width={7} x={14} y={3} />
          <Rect {...stroke} height={9} rx={2} width={7} x={14} y={12} />
          <Rect {...stroke} height={5} rx={2} width={7} x={3} y={16} />
        </>
      ) : name === 'sessions' ? (
        <>
          <Rect {...stroke} height={14} rx={3} width={18} x={3} y={5} />
          <Path {...stroke} d="M7 10l3 2-3 2M12 15h5" />
        </>
      ) : name === 'alerts' ? (
        <Path {...stroke} d="M6 16V11a6 6 0 0 1 12 0v5l2 2H4zM10 21h4" />
      ) : name === 'hosts' ? (
        <>
          <Rect {...stroke} height={12} rx={2} width={18} x={3} y={6} />
          <Path {...stroke} d="M8 21h8M12 18v3" />
        </>
      ) : name === 'plus' ? (
        <Path {...stroke} d="M12 5v14M5 12h14" />
      ) : name === 'send' ? (
        <Path {...stroke} d="M12 19V5M6 11l6-6 6 6" />
      ) : name === 'steer' ? (
        <Path {...stroke} fill={color} d="M13 3L5 13.5h6L10 21l8-10.5h-6z" />
      ) : name === 'queue' ? (
        <Path {...stroke} d="M6 3v7M2.5 6.5h7M13 6.5h8M3 13.5h18M3 19.5h13" />
      ) : name === 'stop' ? (
        <Rect fill={color} height={12} rx={2.5} width={12} x={6} y={6} />
      ) : name === 'arrow' ? (
        <Path {...stroke} d="M5 12h14M13 6l6 6-6 6" />
      ) : name === 'chevronDown' ? (
        <Path {...stroke} d="M6 9l6 6 6-6" />
      ) : name === 'terminal' ? (
        <Path {...stroke} d="M5 7l5 5-5 5M12 18h7" />
      ) : name === 'expand' ? (
        <Path {...stroke} d="M4 9V4h5M15 4h5v5M20 15v5h-5M9 20H4v-5" />
      ) : name === 'collapse' ? (
        <Path {...stroke} d="M9 4v5H4M20 9h-5V4M15 20v-5h5M4 15h5v5" />
      ) : (
        <>
          <Path {...stroke} d="M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z" />
          <Path {...stroke} d="M3 12h18M12 3c2.5 2.7 3.6 5.7 3.6 9s-1.1 6.3-3.6 9c-2.5-2.7-3.6-5.7-3.6-9s1.1-6.3 3.6-9z" />
        </>
      )}
    </Svg>
  );
}
