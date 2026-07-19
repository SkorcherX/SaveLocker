import { useCallback, useEffect, useRef, useState } from 'react'
import { ChevronLeft, Folder, Check, X } from 'lucide-react'
import type { BrowseListing } from '../types'
import { api } from '../api'

interface Props {
  gameName: string
  /** Where to open. The scan's guess for this game, when it has one. */
  initialPath?: string | null
  onConfirm: (path: string) => void
  onCancel: () => void
}

// A Deck is driven with a D-pad and a thumb, not a mouse. Rows are deliberately tall enough to hit
// with a trackpad in Game Mode, and every row is a real <button> so the browser's own focus
// handling does the keyboard work for us.
const ROW_HEIGHT = 44

const OVERLAY: React.CSSProperties = {
  position: 'absolute', inset: 0, background: 'rgba(13,17,20,0.82)',
  display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 50,
}
const PANEL: React.CSSProperties = {
  background: '#1E252A', border: '1px solid #494949', borderRadius: 6,
  width: '86%', maxWidth: 620, maxHeight: '86%',
  display: 'flex', flexDirection: 'column', overflow: 'hidden',
}
const BTN: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6,
  padding: '9px 14px', borderRadius: 4, fontSize: 13, fontWeight: 600,
  cursor: 'pointer', fontFamily: 'inherit', border: 'none',
}

export function PathBrowserModal({ gameName, initialPath, onConfirm, onCancel }: Props) {
  const [listing, setListing] = useState<BrowseListing | null>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)
  const [focusIndex, setFocusIndex] = useState(0)
  const rowRefs = useRef<(HTMLButtonElement | null)[]>([])

  const load = useCallback(async (path: string | null, keepFocus = false) => {
    setLoading(true)
    setError('')
    try {
      const next = await api.browse(path ?? undefined)
      setListing(next)
      if (!keepFocus) setFocusIndex(0)
    } catch (e) {
      // A refused path is the expected failure here (outside the roots, or since deleted), so fall
      // back to the root list rather than leaving the user in a dead modal.
      setError((e as Error).message)
      if (path !== null) {
        try { setListing(await api.browse()) } catch { /* nothing left to show */ }
      }
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void load(initialPath ?? null) }, [load, initialPath])

  // Move real DOM focus with the selection so a controller's D-pad and the focus ring agree.
  useEffect(() => { rowRefs.current[focusIndex]?.focus() }, [focusIndex, listing])

  const entries = listing?.entries ?? []
  const atRootList = !listing?.path

  const goUp = useCallback(() => {
    if (!listing) return
    void load(listing.parent ?? null)
  }, [listing, load])

  const onKeyDown = (e: React.KeyboardEvent) => {
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault()
        setFocusIndex(i => Math.min(i + 1, entries.length - 1))
        break
      case 'ArrowUp':
        e.preventDefault()
        setFocusIndex(i => Math.max(i - 1, 0))
        break
      case 'ArrowRight':
      case 'Enter':
        e.preventDefault()
        if (entries[focusIndex]) void load(entries[focusIndex].path)
        break
      case 'ArrowLeft':
      case 'Backspace':
        e.preventDefault()
        if (!atRootList) goUp()
        break
      case 'Escape':
        e.preventDefault()
        onCancel()
        break
    }
  }

  return (
    <div style={OVERLAY} onKeyDown={onKeyDown}>
      <div style={PANEL}>
        <div style={{ padding: '13px 15px', borderBottom: '1px solid #494949' }}>
          <div style={{ color: '#ECEFF1', fontSize: 14, fontWeight: 600 }}>
            Save folder for {gameName}
          </div>
          <div style={{
            color: '#9CA3AF', fontSize: 11, marginTop: 4,
            fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace",
            wordBreak: 'break-all',
          }}>
            {atRootList ? 'Pick a starting location' : listing?.path}
          </div>
        </div>

        {error && (
          <div style={{ padding: '9px 15px', color: '#f4a60d', fontSize: 12, borderBottom: '1px solid #494949' }}>
            {error}
          </div>
        )}

        <div style={{ flex: 1, overflowY: 'auto', minHeight: 120 }}>
          {!atRootList && (
            <button
              onClick={goUp}
              style={{
                display: 'flex', alignItems: 'center', gap: 9, width: '100%',
                height: ROW_HEIGHT, padding: '0 15px', background: 'transparent',
                border: 'none', borderBottom: '1px solid rgba(73,73,73,0.4)',
                color: '#9CA3AF', fontSize: 13, cursor: 'pointer',
                fontFamily: 'inherit', textAlign: 'left',
              }}
            >
              <ChevronLeft size={15} strokeWidth={1.75} />
              <span>Up one level</span>
            </button>
          )}

          {loading ? (
            <div style={{ padding: '15px', color: '#9CA3AF', fontSize: 12 }}>Loading…</div>
          ) : entries.length === 0 ? (
            <div style={{ padding: '15px', color: '#9CA3AF', fontSize: 12 }}>
              No folders here. Use this folder if the save files live at this level.
            </div>
          ) : entries.map((entry, i) => (
            <button
              key={entry.path}
              ref={el => { rowRefs.current[i] = el }}
              onClick={() => { setFocusIndex(i); void load(entry.path) }}
              onFocus={() => setFocusIndex(i)}
              style={{
                display: 'flex', alignItems: 'center', gap: 9, width: '100%',
                height: ROW_HEIGHT, padding: '0 15px',
                background: i === focusIndex ? '#2A3238' : 'transparent',
                border: 'none', borderBottom: '1px solid rgba(73,73,73,0.4)',
                borderLeft: i === focusIndex ? '3px solid #129271' : '3px solid transparent',
                color: '#ECEFF1', fontSize: 13, cursor: 'pointer',
                fontFamily: 'inherit', textAlign: 'left',
              }}
            >
              <Folder size={15} strokeWidth={1.75} color="#9CA3AF" style={{ flexShrink: 0 }} />
              <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {entry.name}
              </span>
            </button>
          ))}
        </div>

        <div style={{
          display: 'flex', gap: 7, justifyContent: 'flex-end',
          padding: '11px 15px', borderTop: '1px solid #494949',
        }}>
          <button
            onClick={onCancel}
            style={{ ...BTN, background: 'transparent', border: '1px solid #494949', color: '#ECEFF1' }}
          >
            <X size={14} strokeWidth={1.75} />
            <span>Cancel</span>
          </button>
          <button
            onClick={() => listing?.path && onConfirm(listing.path)}
            disabled={atRootList}
            style={{ ...BTN, background: '#129271', color: '#fff', opacity: atRootList ? 0.45 : 1 }}
          >
            <Check size={14} strokeWidth={1.75} />
            <span>Use this folder</span>
          </button>
        </div>
      </div>
    </div>
  )
}
