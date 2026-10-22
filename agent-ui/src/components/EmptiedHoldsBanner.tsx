import { useEffect, useState, type CSSProperties } from 'react'
import { AlertTriangle } from 'lucide-react'
import type { EmptiedHold } from '../types'
import { api } from '../api'

// A game whose save folder was emptied sits out every sync until someone picks one of these. Both are
// recoverable: the version with the files stays on the server either way.
export function EmptiedHoldsBanner() {
  const [holds, setHolds] = useState<EmptiedHold[]>([])
  const [busy, setBusy] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  async function load() {
    try { setHolds(await api.emptiedHolds()) } catch { /* agent restarting; next poll retries */ }
  }

  useEffect(() => {
    void load()
    const t = setInterval(load, 5000)
    return () => clearInterval(t)
  }, [])

  async function answer(h: EmptiedHold, action: 'delete-everywhere' | 'restore-here') {
    if (action === 'delete-everywhere' && !confirm(
      `Delete the save of ${h.gameName} on every machine? Each one removes its copy at its next pull. ` +
      'The last version with files stays on the server.')) return
    setBusy(h.gameId)
    setMessage(null)
    try { setMessage((await api.answerEmptiedHold(h.gameId, action)).message) }
    catch (e) { setMessage(e instanceof Error ? e.message : String(e)) }
    finally { setBusy(null); void load() }
  }

  if (holds.length === 0 && !message) return null

  return (
    <div style={{ flexShrink: 0, padding: '10px 16px 0', display: 'flex', flexDirection: 'column', gap: 8 }}>
      {holds.map(h => (
        <div key={h.gameId} style={{
          display: 'flex', alignItems: 'flex-start', gap: 10,
          background: 'rgba(244,166,13,0.12)', border: '1px solid rgba(244,166,13,0.45)',
          borderRadius: 7, padding: '10px 14px',
        }}>
          <AlertTriangle size={16} strokeWidth={2} color="#f4a60d" style={{ flexShrink: 0, marginTop: 1 }} />
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ color: '#f4a60d', fontSize: 13, fontWeight: 600, lineHeight: 1.3 }}>
              Save files are gone — {h.gameName}
            </div>
            <div style={{ color: '#ECEFF1', fontSize: 12, marginTop: 3, lineHeight: 1.5 }}>
              Every save file disappeared from <code>{h.saveDirectory}</code>. Nothing is pushed or pulled for
              this game until you choose.
            </div>
            <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
              <button style={buttonStyle(busy !== null)} disabled={busy !== null} onClick={() => answer(h, 'restore-here')}>
                Restore here
              </button>
              <button style={buttonStyle(busy !== null)} disabled={busy !== null} onClick={() => answer(h, 'delete-everywhere')}>
                Delete everywhere
              </button>
            </div>
          </div>
        </div>
      ))}
      {message && <div style={{ color: '#9CA3AF', fontSize: 12 }}>{message}</div>}
    </div>
  )
}

function buttonStyle(disabled: boolean): CSSProperties {
  return {
    padding: '4px 11px', background: 'transparent',
    border: '1px solid #494949', borderRadius: 5,
    color: disabled ? '#9CA3AF' : '#ECEFF1', fontSize: 12, fontWeight: 600,
    cursor: disabled ? 'default' : 'pointer', fontFamily: 'inherit',
  }
}
