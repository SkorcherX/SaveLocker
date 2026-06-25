import { useState, useEffect, useCallback } from 'react'
import { RefreshCw, FolderOpen, Cloud } from 'lucide-react'
import type { Candidate } from '../types'
import { api } from '../api'

interface Props {
  onEnrolled: () => void
}

const BTN_BASE: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 5,
  padding: '5px 11px', background: 'transparent',
  border: '1px solid #494949', borderRadius: 4,
  color: '#ECEFF1', fontSize: 12, cursor: 'pointer',
  fontFamily: 'inherit',
}

export function AddGamesView({ onEnrolled }: Props) {
  const [candidates, setCandidates] = useState<Candidate[]>([])
  const [checked, setChecked] = useState<Set<number>>(new Set())
  const [hideSteamCloud, setHideSteamCloud] = useState(false)
  const [scanning, setScanning] = useState(false)
  const [enrolling, setEnrolling] = useState(false)
  const [status, setStatus] = useState('')

  const scan = useCallback(async (force = false) => {
    setScanning(true)
    setStatus('Scanning…')
    try {
      const result = force ? await api.rescan() : await api.candidates()
      setCandidates(result)
      setStatus(`Found ${result.length} candidate(s).`)
    } catch (e) {
      setStatus('Scan failed: ' + (e as Error).message)
    } finally {
      setScanning(false)
    }
  }, [])

  useEffect(() => { void scan(false) }, [scan])

  const toggle = (id: number) => {
    setChecked(prev => {
      const next = new Set(prev)
      next.has(id) ? next.delete(id) : next.add(id)
      return next
    })
  }

  const setSaveFolder = async () => {
    const ids = [...checked]
    if (ids.length !== 1) return
    const id = ids[0]!
    // Uses combined endpoint: opens native dialog on C# side AND updates server cache.
    const result = await api.candidateFolderPick(id)
    if (result.path) {
      setCandidates(prev => prev.map(c => c.id === id ? { ...c, path: result.path! } : c))
    }
  }

  const enroll = async () => {
    if (checked.size === 0) return
    const missing = [...checked].filter(id => {
      const c = candidates.find(c => c.id === id)
      return c && !c.path
    })
    if (missing.length > 0) {
      setStatus('Some selected games have no save folder. Select one and click "Set save folder…".')
      return
    }
    setEnrolling(true)
    setStatus('Enrolling…')
    try {
      const result = await api.enroll([...checked])
      setStatus(
        `Enrolled ${result.enrolled} game(s).` +
        (result.skipped > 0 ? ` Skipped ${result.skipped} already tracked.` : '')
      )
      setChecked(new Set())
      onEnrolled()
      await scan(false)
    } catch (e) {
      setStatus('Enroll failed: ' + (e as Error).message)
    } finally {
      setEnrolling(false)
    }
  }

  const busy = scanning || enrolling
  const visible = hideSteamCloud ? candidates.filter(c => !c.hasSteamCloud) : candidates

  return (
    <div style={{
      position: 'absolute', inset: 0,
      display: 'flex', flexDirection: 'column',
      gap: 11, padding: '16px 20px', overflow: 'hidden',
    }}>
      <p style={{ color: '#9CA3AF', fontSize: 12, lineHeight: 1.65, flexShrink: 0 }}>
        Tick games to sync. Games without a known save folder need one set before enrolling.
      </p>

      {/* Toolbar */}
      <div style={{ display: 'flex', gap: 6, flexShrink: 0, flexWrap: 'wrap' }}>
        <button style={BTN_BASE} disabled={busy} onClick={() => void scan(true)}>
          <RefreshCw size={13} strokeWidth={1.75} color="#9CA3AF" />
          <span>Rescan</span>
        </button>
        <button
          style={{ ...BTN_BASE, opacity: checked.size !== 1 ? 0.45 : 1 }}
          disabled={busy || checked.size !== 1}
          onClick={() => void setSaveFolder()}
        >
          <FolderOpen size={13} strokeWidth={1.75} color="#9CA3AF" />
          <span>Set save folder…</span>
        </button>
        <button
          onClick={() => setHideSteamCloud(h => !h)}
          style={{
            display: 'flex', alignItems: 'center', gap: 5, padding: '5px 11px',
            background: hideSteamCloud ? 'rgba(18,146,113,0.1)' : 'transparent',
            border: `1px solid ${hideSteamCloud ? '#129271' : '#494949'}`,
            borderRadius: 4,
            color: hideSteamCloud ? '#129271' : '#9CA3AF',
            fontSize: 12, cursor: 'pointer', fontFamily: 'inherit',
          }}
        >
          <Cloud size={13} strokeWidth={1.75} color={hideSteamCloud ? '#129271' : '#9CA3AF'} />
          <span>Hide Steam Cloud</span>
        </button>
      </div>

      {/* Game list */}
      <div style={{
        background: '#1E252A', border: '1px solid #494949', borderRadius: 6,
        overflowY: 'auto', flex: 1, minHeight: 0,
      }}>
        {visible.map(c => (
          <div
            key={c.id}
            style={{
              display: 'flex', alignItems: 'flex-start',
              padding: '10px 13px',
              borderBottom: '1px solid rgba(73,73,73,0.4)',
              gap: 10,
            }}
          >
            <input
              type="checkbox"
              checked={checked.has(c.id)}
              onChange={() => toggle(c.id)}
              style={{ marginTop: 2 }}
            />
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
                <span style={{ color: '#ECEFF1', fontSize: 13, fontWeight: 500 }}>{c.name}</span>
                <span style={{
                  color: '#9CA3AF', fontSize: 10,
                  background: 'rgba(255,255,255,0.05)',
                  border: '1px solid rgba(255,255,255,0.09)',
                  padding: '1px 6px', borderRadius: 3,
                  fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace",
                }}>
                  {c.source}
                </span>
                {c.hasSteamCloud && (
                  <span style={{
                    color: '#60a5fa', fontSize: 10,
                    background: 'rgba(96,165,250,0.08)',
                    border: '1px solid rgba(96,165,250,0.22)',
                    padding: '1px 6px', borderRadius: 3,
                  }}>
                    Steam Cloud
                  </span>
                )}
              </div>
              {c.path && (
                <div style={{
                  color: '#9CA3AF', fontSize: 10, marginTop: 3,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace",
                }}>
                  {c.path}
                </div>
              )}
            </div>
          </div>
        ))}
      </div>

      {/* Footer */}
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexShrink: 0 }}>
        <span style={{ color: '#9CA3AF', fontSize: 12 }}>
          {status || `Found ${visible.length} candidate(s).`}
        </span>
        <button
          onClick={() => void enroll()}
          disabled={busy || checked.size === 0}
          style={{
            padding: '7px 18px', background: '#129271', border: 'none', borderRadius: 5,
            color: '#fff', fontSize: 13, fontWeight: 600,
            cursor: checked.size > 0 && !busy ? 'pointer' : 'default',
            fontFamily: 'inherit', letterSpacing: '0.01em',
            opacity: checked.size === 0 || busy ? 0.5 : 1,
          }}
        >
          Enroll selected
        </button>
      </div>
    </div>
  )
}
