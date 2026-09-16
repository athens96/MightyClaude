export function BrandMark({ size = 25, className = '' }: { size?: number; className?: string }) {
  return (
    <svg width={size} height={size} viewBox="0 0 32 32" fill="none" className={className} aria-hidden="true">
      <path d="M5 24V9.5L10 7L16 17.5L22 7L27 9.5V24L22 27V16L16 26L10 16V27L5 24Z" fill="currentColor" />
    </svg>
  )
}
