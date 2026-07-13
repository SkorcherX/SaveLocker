import { useState, useEffect, useCallback, useRef } from 'react'
import { FolderOpen, Trash2, Copy } from 'lucide-react'
import type { AgentState, TrackedGame } from '../types'
import { api } from '../api'

interface Props {
  state: AgentState | null
  onSaved: () => void
}

const INPUT: React.CSSProperties = {
  background: '#1E252A', border: '1px solid #494949', borderRadius: 4,
  padding: '7px 10px', color: '#ECEFF1', outline: 'none', fontFamily: 'inherit',
}
const LABEL: React.CSSProperties = {
  color: '#9CA3AF', fontSize: 11, display: 'block', marginBottom: 5,
}
const BTN_PRIMARY: React.CSSProperties = {
  padding: '7px 15px', background: '#129271', border: 'none', borderRadius: 4,
  color: '#fff', fontSize: 12, fontWeight: 600, cursor: 'pointer',
  fontFamily: 'inherit', flexShrink: 0,
}
const BTN_SECONDARY: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 5,
  padding: '7px 11px', background: 'transparent',
  border: '1px solid #494949', borderRadius: 4,
  color: '#ECEFF1', fontSize: 12, cursor: 'pointer',
  fontFamily: 'inherit', flexShrink: 0, whiteSpace: 'nowrap',
}
const SECTION_HEADER: React.CSSProperties = {
  color: '#9CA3AF', fontSize: 10, textTransform: 'uppercase', letterSpacing: '0.11em',
  marginBottom: 14, paddingBottom: 8, borderBottom: '1px solid #494949',
}

export function SettingsView({ state, onSaved }: Props) {
  const [serverUrl, setServerUrl] = useState('')
  const [machineName, setMachineName] = useState('')
  const [adminPassword, setAdminPassword] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [startWithWindows, setStartWithWindows] = useState(false)
  const [settleQuietSeconds, setSettleQuietSeconds] = useState('10')
  const [copied, setCopied] = useState(false)
  const [games, setGames] = useState<TrackedGame[]>([])
  const [selectedGames, setSelectedGames] = useState<Set<string>>(new Set())
  const [saving, setSaving] = useState(false)
  const [registering, setRegistering] = useState(false)
  const [status, setStatus] = useState('')
  const dirtyFields = useRef<Set<string>>(new Set())

  useEffect(() => {
    if (state) {
      if (!dirtyFields.current.has('serverUrl')) setServerUrl(state.serverUrl)
      if (!dirtyFields.current.has('machineName')) setMachineName(state.machineName)
      if (!dirtyFields.current.has('settleQuietSeconds'))
        setSettleQuietSeconds(String(state.settleQuietSeconds))
      setApiKey(state.apiKey)
      setStartWithWindows(state.startWithWindows)
    }
  }, [state])

  const loadGames = useCallback(() => {
    api.games().then(setGames).catch(console.error)
  }, [])

  useEffect(() => { loadGames() }, [loadGames])

  const save = async () => {
    setSaving(true)
    try {
      const seconds = parseInt(settleQuietSeconds, 10)
      await api.saveConfig({
        serverUrl,
        machineName,
        settleQuietSeconds: Number.isFinite(seconds) ? Math.min(Math.max(seconds, 0), 300) : undefined,
      })
      dirtyFields.current.clear()
      onSaved()
      setStatus('Saved.')
      setTimeout(() => setStatus(''), 2000)
    } catch (e) {
      setStatus('Save failed: ' + (e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  const register = async () => {
    setRegistering(true)
    setStatus('Registering…')
    try {
      await api.saveConfig({ serverUrl, machineName })
      const result = await api.register(adminPassword || undefined)
      setApiKey(result.apiKey)
      setAdminPassword('')
      dirtyFields.current.clear()
      onSaved()
      setStatus('Registered successfully.')
    } catch (e) {
      setStatus('Registration failed: ' + (e as Error).message)
    } finally {
      setRegistering(false)
    }
  }

  const copyKey = () => {
    navigator.clipboard.writeText(apiKey).catch(() => {})
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  const toggleStartup = async (val: boolean) => {
    setStartWithWindows(val)
    await api.saveConfig({ startWithWindows: val }).catch(console.error)
  }

  const toggleGame = (id: string) => {
    setSelectedGames(prev => {
      const next = new Set(prev)
      next.has(id) ? next.delete(id) : next.add(id)
      return next
    })
  }

  const removeSelected = async () => {
    for (const id of selectedGames) await api.removeGame(id)
    setSelectedGames(new Set())
    loadGames()
    onSaved()
  }

  const setGameFolder = async () => {
    if (selectedGames.size !== 1) return
    const id = [...selectedGames][0]!
    const result = await api.folderPick()
    if (result.path) {
      await api.setGameFolder(id, result.path)
      loadGames()
    }
  }

  const busy = saving || registering

  return (
    <div style={{
      position: 'absolute', inset: 0, overflowY: 'auto',
      padding: '18px 20px', display: 'flex', flexDirection: 'column', gap: 22,
    }}>
      {/* Connection */}
      <div>
        <div style={SECTION_HEADER}>Connection</div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 11 }}>

          <div>
            <label style={LABEL}>Server URL</label>
            <div style={{ display: 'flex', gap: 6 }}>
              <input
                type="text" value={serverUrl} onChange={e => { dirtyFields.current.add('serverUrl'); setServerUrl(e.target.value) }}
                style={{ ...INPUT, flex: 1, minWidth: 0, fontSize: 12, fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace" }}
              />
              <button style={BTN_PRIMARY} onClick={() => void save()} disabled={busy}>Save</button>
              <button style={BTN_SECONDARY} onClick={() => void register()} disabled={busy}>
                Register / Re-register
              </button>
            </div>
          </div>

          <div>
            <label style={LABEL}>Machine Name</label>
            <input
              type="text" value={machineName} onChange={e => { dirtyFields.current.add('machineName'); setMachineName(e.target.value) }}
              style={{ ...INPUT, width: 240, fontSize: 13 }}
            />
          </div>

          <div>
            <label style={LABEL}>Admin Password</label>
            <input
              type="password" value={adminPassword} onChange={e => setAdminPassword(e.target.value)}
              placeholder="only needed to re-register this name"
              autoComplete="off"
              style={{ ...INPUT, width: 240, fontSize: 13 }}
            />
          </div>

          <div>
            <label style={LABEL}>API Key</label>
            <div style={{ display: 'flex', gap: 6 }}>
              <input
                type="text" value={apiKey} readOnly
                style={{ ...INPUT, flex: 1, minWidth: 0, fontSize: 12, fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace" }}
              />
              <button
                onClick={copyKey}
                style={{
                  display: 'flex', alignItems: 'center', gap: 5, padding: '7px 12px',
                  background: copied ? 'rgba(18,146,113,0.1)' : 'transparent',
                  border: `1px solid ${copied ? '#129271' : '#494949'}`,
                  borderRadius: 4, color: copied ? '#129271' : '#ECEFF1',
                  fontSize: 12, cursor: 'pointer', fontFamily: 'inherit',
                  flexShrink: 0, whiteSpace: 'nowrap',
                }}
              >
                <Copy size={13} strokeWidth={1.75} color={copied ? '#129271' : '#9CA3AF'} />
                <span>{copied ? '✓ Copied' : 'Copy'}</span>
              </button>
            </div>
          </div>

          <label style={{ display: 'flex', alignItems: 'center', gap: 8, cursor: 'pointer', userSelect: 'none' }}>
            <input
              type="checkbox" checked={startWithWindows}
              onChange={e => void toggleStartup(e.target.checked)}
            />
            <span style={{ color: '#ECEFF1', fontSize: 13 }}>Start with Windows (launch agent at login)</span>
          </label>
        </div>

        {status && (
          <div style={{ color: '#9CA3AF', fontSize: 12, marginTop: 8 }}>{status}</div>
        )}
      </div>

      {/* Sync Safety */}
      <div>
        <div style={SECTION_HEADER}>Sync Safety</div>
        <label style={LABEL}>Wait for saves to settle (seconds)</label>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <input
            type="number" min={0} max={300}
            value={settleQuietSeconds}
            onChange={e => { dirtyFields.current.add('settleQuietSeconds'); setSettleQuietSeconds(e.target.value) }}
            style={{ ...INPUT, width: 80, fontSize: 13 }}
          />
          <button style={BTN_PRIMARY} onClick={() => void save()} disabled={busy}>Save</button>
        </div>
        <div style={{ color: '#9CA3AF', fontSize: 11, marginTop: 7, lineHeight: 1.5 }}>
          After a game closes, SaveLocker waits until its save folder stops changing for this long
          before backing it up — so a game that keeps writing for a few seconds after exit can't be
          captured half-finished. Raise it if a game is slow to flush its save. 0 backs up
          immediately. Manual syncs are never delayed.
        </div>
      </div>

      {/* Tracked Games */}
      <div>
        <div style={SECTION_HEADER}>Currently Tracked Games</div>

        <div style={{
          background: '#1E252A', border: '1px solid #494949', borderRadius: 5,
          overflow: 'hidden', marginBottom: 10,
        }}>
          {games.length === 0 ? (
            <div style={{ padding: '14px 13px', color: '#9CA3AF', fontSize: 12 }}>
              No games tracked yet. Go to Add Games to enroll.
            </div>
          ) : games.map(g => (
            <div
              key={g.id}
              style={{
                display: 'flex', alignItems: 'flex-start',
                padding: '10px 13px',
                borderBottom: '1px solid rgba(73,73,73,0.4)',
                gap: 10,
              }}
            >
              <input
                type="checkbox"
                checked={selectedGames.has(g.id)}
                onChange={() => toggleGame(g.id)}
                style={{ marginTop: 2 }}
              />
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ color: '#ECEFF1', fontSize: 13, fontWeight: 500, marginBottom: 3 }}>
                  {g.name}
                </div>
                {g.path && (
                  <div style={{
                    color: '#9CA3AF', fontSize: 10,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace",
                  }}>
                    {g.path}
                  </div>
                )}
              </div>
            </div>
          ))}
        </div>

        <div style={{ display: 'flex', gap: 6 }}>
          <button
            onClick={() => void setGameFolder()}
            disabled={selectedGames.size !== 1}
            style={{
              ...BTN_SECONDARY,
              opacity: selectedGames.size !== 1 ? 0.45 : 1,
            }}
          >
            <FolderOpen size={13} strokeWidth={1.75} color="#9CA3AF" />
            <span>Set save folder…</span>
          </button>
          <button
            onClick={() => void removeSelected()}
            disabled={selectedGames.size === 0}
            style={{
              display: 'flex', alignItems: 'center', gap: 5,
              padding: '6px 12px', background: 'transparent',
              border: '1px solid #f4a60d', borderRadius: 4,
              color: '#f4a60d', fontSize: 12, cursor: 'pointer',
              fontFamily: 'inherit',
              opacity: selectedGames.size === 0 ? 0.45 : 1,
            }}
          >
            <Trash2 size={13} strokeWidth={1.75} color="#f4a60d" />
            <span>Remove selected</span>
          </button>
        </div>
      </div>
    </div>
  )
}
