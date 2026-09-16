import { useEffect, useRef, type ReactNode } from 'react'
import { X } from 'lucide-react'

export function Modal({ title, description, children, onClose, className = '' }: {
  title: string
  description?: string
  children: ReactNode
  onClose: () => void
  className?: string
}) {
  const dialogRef = useRef<HTMLDivElement>(null)
  const closeRef = useRef(onClose)
  closeRef.current = onClose
  useEffect(() => {
    const previousFocus = document.activeElement as HTMLElement | null
    const container = dialogRef.current
    const getFocusable = () => Array.from(container?.querySelectorAll<HTMLElement>('button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex="0"]') ?? [])
    const initial = container?.querySelector<HTMLElement>('[data-autofocus]') ?? getFocusable()[0]
    initial?.focus()
    const handleKey = (event: KeyboardEvent) => {
      if (event.isComposing) return
      if (event.key === 'Escape') {
        event.preventDefault()
        event.stopPropagation()
        closeRef.current()
      }
      if (event.key !== 'Tab') return
      const elements = getFocusable()
      const first = elements[0]
      const last = elements[elements.length - 1]
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus() }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus() }
    }
    document.addEventListener('keydown', handleKey, true)
    return () => { document.removeEventListener('keydown', handleKey, true); previousFocus?.focus() }
  }, [])

  return (
    <div className="modal-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
      <div ref={dialogRef} role="dialog" aria-modal="true" aria-labelledby="dialog-title" aria-describedby={description ? 'dialog-description' : undefined} className={`modal ${className}`}>
        <div className="modal-heading">
          <div>
            <h2 id="dialog-title">{title}</h2>
            {description && <p id="dialog-description">{description}</p>}
          </div>
          <button type="button" className="icon-button" aria-label="대화상자 닫기" onClick={onClose}><X size={17} /></button>
        </div>
        {children}
      </div>
    </div>
  )
}
