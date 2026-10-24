import { createElement, memo } from 'react';
import { lucide, type IconName } from './lucide';

export type { IconName } from './lucide';

const camel = (name: string) => name.replace(/-([a-z])/g, (_, c: string) => c.toUpperCase());

/**
 * One Lucide icon (src/icons/lucide.ts) in the place of the SF Symbol the Mac draws there, at the Mac's point size and in the
 * current ink. `weight` is the stroke at 24 units (Lucide's 2; thinner reads closer to SF Symbols at small sizes).
 */
export const Icon = memo(function Icon({ name, size = 14, weight = 1.75, className, fill }: { name: IconName; size?: number; weight?: number; className?: string; fill?: boolean }) {
  return (
    <svg className={'icon' + (className ? ' ' + className : '')} width={size} height={size} viewBox="0 0 24 24" fill={fill ? 'currentColor' : 'none'}
      stroke="currentColor" strokeWidth={weight} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
      {lucide[name].map(([tag, attrs], index) => createElement(tag, { key: index, ...Object.fromEntries(Object.entries(attrs).map(([k, v]) => [camel(k), v])) }))}
    </svg>
  );
});
