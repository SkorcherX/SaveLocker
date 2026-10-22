import { useCallback, useEffect, useState, type CSSProperties } from 'react'
import { FolderPlus } from 'lucide-react'
import type { FolderSuggestion } from '../types'
import { api } from '../api'

// "Also found": folders the manifest says a tracked game also keeps saves in, which exist here and are not
// synced yet. Adding one defines it for every machine; "Don't sync" never asks again; "Not now" stops it
// being asked about here until it is looked at again. Nothing is added unasked.
export function FolderSuggestionsCard() {
  const [suggestions, setSuggestions] = useState<FolderSuggestion[]>([])
  const [busy, setBusy] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  const load = useCallback(async () => {
    try { setSuggestions((await api.folderSuggestions()).filter(s => !s.deferred)) }
    catch { /* agent restarting, or not registered yet: the next load retries */ }
  }, [])

  useEffect(() => { void load() }, [load])

  async function run(s: FolderSuggestion, work: () => Promise<string | void>) {
    setBusy(s.path)
    setMessage(null)
    try { const m = await work(); if (m) setMessage(m) }
    catch (e) { setMessage(e instanceof Error ? e.message : String(e)) }
    finally { setBusy(null); void load() }
  }

  const add = (s: FolderSuggestion) => run(s, async () => {
    const r = await api.addSaveFolder(s.gameId, s.path, s.key)
    return r.joined
      ? `${s.gameName} already syncs that folder from another device; this one joined it as '${r.key}'.`
      : `${s.gameName} now syncs '${r.key}' on every device.`
  })
  const ignore = (s: FolderSuggestion) => run(s, () => api.answerFolderSuggestions(s.gameId, { ignore: [s.path] }).then(() => {}))
  const defer = (s: FolderSuggestion) => run(s, () => api.answerFolderSuggestions(s.gameId, { defer: [s.path] }).then(() => {}))

  if (suggestions.length === 0 && !message) return null

  return (
    <div style={{ flexShrink: 0, padding: '10px 16px 0', display: 'flex', flexDirection: 'column', gap: 8 }}>
      {suggestions.length > 0 && (
        <div style={{
          background: 'rgba(18,146,113,0.08)', border: '1px solid rgba(18,146,113,0.4)',
          borderRadius: 7, padding: '10px 14px',
        }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, color: '#ECEFF1', fontSize: 13, fontWeight: 600 }}>
            <FolderPlus size={15} strokeWidth={2} color="#129271" />
            More save folders found
          </div>
          <div style={{ color: '#9CA3AF', fontSize: 12, marginTop: 3, lineHeight: 1.5 }}>
            These games also keep files in folders that aren't synced yet. Check each one is really part of the save.
          </div>
          {suggestions.map(s => (
            <div key={`${s.gameId}:${s.path}`} style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 8 }}>
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ color: '#ECEFF1', fontSize: 12 }}>{s.gameName} <span style={{ color: '#9CA3AF' }}>· {s.key}</span></div>
                <div style={{
                  color: '#9CA3AF', fontSize: 10, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace",
                }} title={s.path}>{s.path}</div>
              </div>
              <button style={button(busy !== null, true)} disabled={busy !== null} onClick={() => void add(s)}>
                {busy === s.path ? 'Adding…' : 'Sync it'}
              </button>
              <button style={button(busy !== null)} disabled={busy !== null} onClick={() => void ignore(s)}>Don't sync</button>
              <button style={button(busy !== null)} disabled={busy !== null} onClick={() => void defer(s)}>Not now</button>
            </div>
          ))}
        </div>
      )}
      {message && <div style={{ color: '#9CA3AF', fontSize: 12 }}>{message}</div>}
    </div>
  )
}

function button(disabled: boolean, primary = false): CSSProperties {
  return {
    flexShrink: 0, padding: '4px 11px', borderRadius: 5, fontSize: 12, fontWeight: 600, fontFamily: 'inherit',
    background: primary ? '#129271' : 'transparent', color: primary ? '#fff' : disabled ? '#9CA3AF' : '#ECEFF1',
    border: `1px solid ${primary ? '#129271' : '#494949'}`, cursor: disabled ? 'default' : 'pointer',
    opacity: disabled ? 0.6 : 1,
  }
}
