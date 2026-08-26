import { useState, useEffect, useCallback, useRef } from 'react'
import { FolderSearch, Trash2 } from 'lucide-react'
import type { AgentState, AgentVersion, TrackedGame } from '../types'
import { api } from '../api'
import { useFolderPicker } from '../useFolderPicker'
import { PathBrowserModal } from './PathBrowserModal'

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

/**
 * What the agent's last update check found.
 *
 * On Windows the tray owns the actual update — this is a read-out of what it already knows, and the
 * tray is where the user says yes. On Linux there is no tray and nothing that can toast, so this and
 * `savelocker doctor` are the only places a Deck user can see the state of an update at all.
 *
 * **Staged and available are different states and are shown differently.** Available means the
 * server is offering something newer and nothing has been downloaded. Staged means the payload is
 * already here, checked against the published SHA-256 and smoke-tested, and the only thing left is
 * the restart — so it gets the green badge and the instruction, while available gets neither.
 * `stagedVersion` is null on Windows, where the installer path stages nothing.
 */
function UpdateStatus({ platform }: { platform?: string }) {
  const [info, setInfo] = useState<AgentVersion | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    api.agentVersion().then(setInfo).catch(() => setFailed(true))
  }, [])

  if (failed) return <div style={{ color: '#9CA3AF', fontSize: 12 }}>Could not read the agent version.</div>
  if (!info) return <div style={{ color: '#9CA3AF', fontSize: 12 }}>Checking…</div>

  const badge = (text: string, background: string) => (
    <span style={{
      padding: '2px 7px', background, color: '#1E252A', borderRadius: 4,
      fontSize: 10, fontWeight: 700, letterSpacing: '0.04em',
    }}>{text}</span>
  )

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <span style={{ color: '#ECEFF1', fontSize: 13 }}>Running v{info.currentVersion}</span>
        {info.stagedVersion
          ? badge(`v${info.stagedVersion} READY`, '#4ade80')
          : info.updateAvailable && info.latestVersion
            ? badge(`v${info.latestVersion} AVAILABLE`, '#f4a60d')
            : null}
      </div>
      <div style={{ color: '#9CA3AF', fontSize: 11, lineHeight: 1.5 }}>
        {info.stagedVersion
          // The agent's own words for what blocks it, verbatim: it knows which game is running and
          // this UI does not, and re-phrasing it here is how three surfaces start disagreeing.
          ? info.stagedBlockedReason
            ?? 'It is downloaded and verified, and installs the next time the agent starts — a ' +
               'reboot, or a log out and back in. Nothing is replaced under a running session.'
          : !info.updateAvailable
            ? 'This is the version the server is offering.'
            : platform === 'Linux'
              ? 'The agent downloads and verifies it on its next check (every few hours), then ' +
                'installs it at the following start. Run `savelocker update` to do it now.'
              : 'The tray icon offers the update — accept it there to install and restart.'}
      </div>
    </div>
  )
}

export function SettingsView({ state, onSaved }: Props) {
  const [serverUrl, setServerUrl] = useState('')
  const [machineName, setMachineName] = useState('')
  const [adminPassword, setAdminPassword] = useState('')
  const [startWithWindows, setStartWithWindows] = useState(false)
  const [settleQuietSeconds, setSettleQuietSeconds] = useState('10')
  const [games, setGames] = useState<TrackedGame[]>([])
  const [selectedGames, setSelectedGames] = useState<Set<string>>(new Set())
  const [saving, setSaving] = useState(false)
  const [registering, setRegistering] = useState(false)
  const [status, setStatus] = useState('')
  const picker = useFolderPicker()
  const dirtyFields = useRef<Set<string>>(new Set())

  useEffect(() => {
    if (state) {
      if (!dirtyFields.current.has('serverUrl')) setServerUrl(state.serverUrl)
      if (!dirtyFields.current.has('machineName')) setMachineName(state.machineName)
      if (!dirtyFields.current.has('settleQuietSeconds'))
        setSettleQuietSeconds(String(state.settleQuietSeconds))
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
      const res = await api.saveConfig({
        serverUrl,
        machineName,
        settleQuietSeconds: Number.isFinite(seconds) ? Math.min(Math.max(seconds, 0), 300) : undefined,
      })
      dirtyFields.current.clear()
      onSaved()
      if (res.identityCleared) {
        // Left on screen rather than auto-cleared: the agent cannot sync until the user acts on it,
        // and a message that vanishes after two seconds is how someone ends up staring at a
        // disconnected agent with no idea what changed.
        setStatus('Saved. This is a different server, so the stored machine key was cleared — ' +
                  'click Register / Re-register to enroll this machine with it.')
      } else {
        setStatus('Saved.')
        setTimeout(() => setStatus(''), 2000)
      }
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
      await api.register(adminPassword || undefined)
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

  // The toggle shows what the machine will actually do at login, not what was clicked. The registry
  // write can be refused outright (group policy, security software) or written and then reverted,
  // and the box used to stay ticked through both. WA-10.
  const toggleStartup = async (val: boolean) => {
    setStartWithWindows(val)
    try {
      const res = await api.saveConfig({ startWithWindows: val })
      setStartWithWindows(res.startWithWindows)
      setStatus(res.startWithWindows === val
        ? ''
        : 'Windows did not keep that startup setting.')
    } catch (e) {
      setStartWithWindows(!val)
      setStatus((e as Error).message)
    }
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

  // Settings is edit-only now: the save folder is *first* set in Add Games (enrollment is gated on
  // it there). Native dialog first — on Windows the tray keeps the Explorer dialog it always had,
  // which can reach paths the browser deliberately cannot — falling through to the in-app browser
  // when there is no dialog (a headless Deck), where the browser opens at the game's current path
  // or its scan-time suggestion.
  const pickFolderFor = (game: TrackedGame) => picker.pick({
    name: game.name,
    start: async () => game.path
      || (await api.suggestedPath(game.id).catch(() => ({ path: null }))).path,
    nativePick: () => api.folderPick(),
    apply: async (path) => {
      const send = async (confirm: boolean) => {
        await api.setGameFolder(game.id, path, confirm)
        loadGames()
        onSaved()
        setStatus(`Save folder for ${game.name} set to ${path}`)
        setTimeout(() => setStatus(''), 4000)
      }
      try {
        await send(false)
      } catch (e) {
        const message = (e as Error).message
        // The agent asks for confirmation only for the heuristic warnings, which have false
        // positives. Hard refusals never carry this sentence, so they can never be clicked past —
        // and the prompt repeats the agent's own wording rather than a cheerful paraphrase, because
        // the whole point is that the user reads what is actually wrong.
        if (message.includes('Re-send with confirm')) {
          const ask = message.replace(' Re-send with confirm to use it anyway.', '')
          if (!window.confirm(`${ask}\n\nUse ${path} anyway?`)) {
            setStatus('Save folder unchanged.')
            return
          }
          try { await send(true) }
          catch (e2) { setStatus('Could not set the save folder: ' + (e2 as Error).message) }
          return
        }
        setStatus('Could not set the save folder: ' + message)
      }
    },
  })

  // A prompt rather than an inline field: this is an occasional correction, not something the user
  // edits while reading the list, and a text input per row would crowd out the save path.
  const editProcessesFor = async (game: TrackedGame) => {
    const current = game.processNames.join(', ')
    const next = window.prompt(
      `Which process means "${game.name}" is running?\n\n` +
      'Use the executable name, e.g. "stardew valley" or "game.exe". ' +
      'Separate several with commas.\n\n' +
      'Until this is set, SaveLocker cannot take a lease when you launch, push when you quit, ' +
      'or stop a pull from overwriting saves while the game is open.',
      current)
    if (next === null) return

    try {
      await api.setGameProcesses(game.id, next.split(',').map(s => s.trim()).filter(Boolean))
      loadGames()
      onSaved()
      setStatus(`Launch/exit sync for ${game.name} updated.`)
      setTimeout(() => setStatus(''), 4000)
    } catch (e) {
      setStatus('Could not set the game process: ' + (e as Error).message)
    }
  }

  const startupLabel = state?.platform === 'Linux'
    ? 'Start on login (launch agent when you sign in)'
    : 'Start with Windows (launch agent at login)'

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
            <label style={LABEL}>Connection Status</label>
            <div style={{ color: state?.connected ? '#129271' : '#f4a60d', fontSize: 13 }}>
              {state?.connected
                ? 'Registered — this machine holds a key for the server.'
                : 'Not registered yet.'}
            </div>
            <div style={{ color: '#9CA3AF', fontSize: 11, marginTop: 5, lineHeight: 1.5 }}>
              The machine key is kept in the agent's config file and never shown here. If it is ever
              exposed, use Register / Re-register above to rotate it.
            </div>
          </div>

          <label style={{ display: 'flex', alignItems: 'center', gap: 8, cursor: 'pointer', userSelect: 'none' }}>
            <input
              type="checkbox" checked={startWithWindows}
              onChange={e => void toggleStartup(e.target.checked)}
            />
            <span style={{ color: '#ECEFF1', fontSize: 13 }}>{startupLabel}</span>
          </label>
        </div>

        {status && (
          <div style={{ color: '#9CA3AF', fontSize: 12, marginTop: 8 }}>{status}</div>
        )}
      </div>

      {/* Updates */}
      <div>
        <div style={SECTION_HEADER}>Updates</div>
        <UpdateStatus platform={state?.platform} />
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
                    color: '#9CA3AF', fontSize: 10, marginBottom: 5,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace",
                  }}>
                    {g.path}
                  </div>
                )}
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: g.path ? 0 : 3 }}>
                  {/* An unmapped game (enrolled before Add Games gated on a folder) needs a path
                      set; a mapped one only ever needs it changed. Distinct labels, and neither
                      collides with Add Games' "Set save folder". */}
                  {!g.path && <span style={{ color: '#f4a60d', fontSize: 11 }}>No save folder set</span>}
                  <button
                    onClick={() => void pickFolderFor(g)}
                    style={{
                      display: 'flex', alignItems: 'center', gap: 5,
                      padding: '5px 10px', background: 'transparent',
                      border: `1px solid ${g.path ? '#494949' : '#129271'}`, borderRadius: 4,
                      color: g.path ? '#9CA3AF' : '#129271', fontSize: 11, fontWeight: 600,
                      cursor: 'pointer', fontFamily: 'inherit',
                    }}
                  >
                    <FolderSearch size={12} strokeWidth={1.75} />
                    <span>{g.path ? 'Change save path' : 'Set save path'}</span>
                  </button>
                </div>

                {/* Launch/exit sync state, stated honestly. An empty process list means the
                    watcher excludes this game entirely — no lease, no push when you quit, and no
                    refusal to overwrite saves while it is running. Claiming automatic sync here
                    would be a lie, so the row says which it is and offers the fix. WA-08. */}
                {state?.platform !== 'Linux' && (
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 6 }}>
                    {g.processNames.length > 0 ? (
                      <span style={{ color: '#9CA3AF', fontSize: 10 }}>
                        Launch/exit sync: {g.processNames.join(', ')}
                      </span>
                    ) : (
                      <span style={{ color: '#f4a60d', fontSize: 11 }}>
                        Launch/exit sync not configured
                      </span>
                    )}
                    <button
                      onClick={() => void editProcessesFor(g)}
                      style={{
                        padding: '4px 9px', background: 'transparent',
                        border: `1px solid ${g.processNames.length > 0 ? '#494949' : '#f4a60d'}`,
                        borderRadius: 4,
                        color: g.processNames.length > 0 ? '#9CA3AF' : '#f4a60d',
                        fontSize: 11, cursor: 'pointer', fontFamily: 'inherit',
                      }}
                    >
                      {g.processNames.length > 0 ? 'Edit' : 'Set game process'}
                    </button>
                  </div>
                )}
              </div>
            </div>
          ))}
        </div>

        <div style={{ display: 'flex', gap: 6 }}>
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

      {picker.browsing && (
        <PathBrowserModal
          gameName={picker.browsing.name}
          initialPath={picker.browsing.start}
          onConfirm={path => void picker.confirmBrowsed(path)}
          onCancel={picker.cancel}
        />
      )}
    </div>
  )
}
