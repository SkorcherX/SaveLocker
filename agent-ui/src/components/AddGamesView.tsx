import { useState, useEffect, useCallback, useMemo } from 'react'
import { RefreshCw, FolderOpen, FolderSearch } from 'lucide-react'
import type { Candidate } from '../types'
import { api } from '../api'
import { useFolderPicker } from '../useFolderPicker'
import { PathBrowserModal } from './PathBrowserModal'
import { LaunchSetupCard } from './LaunchSetupCard'

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

/**
 * The list filters, in the order they appear.
 *
 * `suggested` is the default and is NOT the same as `all`: SaveLocker exists for games Steam does
 * not back up, so a library of hundreds of Cloud-backed titles would otherwise bury the handful the
 * user came to enroll. It is a starting view, not a restriction — `all` is one click away, which is
 * the part the Linux agent was missing entirely: it filtered Cloud games out with no control to
 * bring them back, so an installed Steam game could not be reached at all.
 */
type FilterId = 'suggested' | 'all' | 'steam' | 'shortcut' | 'heroic' | 'nopath'

const FILTERS: { id: FilterId; label: string; hint: string; match: (c: Candidate) => boolean }[] = [
  { id: 'suggested', label: 'Suggested', hint: 'Everything except games Steam Cloud already backs up', match: c => !c.hasSteamCloud },
  { id: 'all', label: 'All', hint: 'Every game found, including Steam Cloud titles', match: () => true },
  { id: 'steam', label: 'Steam', hint: 'Games installed from the Steam store', match: c => c.source === 'SteamInstalled' },
  { id: 'shortcut', label: 'Added to Steam', hint: 'Non-Steam games you added to your Steam library', match: c => c.source === 'SteamShortcut' },
  { id: 'heroic', label: 'Heroic', hint: 'Games installed through Heroic Games Launcher', match: c => c.source === 'Heroic' },
  // Not a source but a state, and the one that costs the user the most time: these are the games
  // absent from the Ludusavi manifest, so each needs a save folder set by hand before it can enroll.
  { id: 'nopath', label: 'Needs path', hint: 'No save folder could be detected — set one by hand', match: c => !c.path },
]

/** Heroic's storefronts, as a second axis under the Heroic filter. `Unknown` covers a runner we don't map. */
const STORES: { id: string; label: string }[] = [
  { id: 'Epic', label: 'Epic' },
  { id: 'Gog', label: 'GOG' },
  { id: 'Amazon', label: 'Amazon' },
  { id: 'Sideload', label: 'Sideloaded' },
  { id: 'Unknown', label: 'Other' },
]

function chipStyle(active: boolean): React.CSSProperties {
  return {
    display: 'flex', alignItems: 'center', gap: 5, padding: '4px 10px',
    background: active ? 'rgba(18,146,113,0.12)' : 'transparent',
    border: `1px solid ${active ? '#129271' : '#494949'}`,
    borderRadius: 999,
    color: active ? '#129271' : '#9CA3AF',
    fontSize: 11.5, cursor: 'pointer', fontFamily: 'inherit',
    whiteSpace: 'nowrap',
  }
}

export function AddGamesView({ onEnrolled }: Props) {
  const [candidates, setCandidates] = useState<Candidate[]>([])
  const [checked, setChecked] = useState<Set<number>>(new Set())
  const [filter, setFilter] = useState<FilterId>('suggested')
  const [store, setStore] = useState<string | null>(null)
  const [scanning, setScanning] = useState(false)
  const [enrolling, setEnrolling] = useState(false)
  const [status, setStatus] = useState('')
  const [enrolled, setEnrolled] = useState(false)
  const picker = useFolderPicker()

  const scan = useCallback(async (force = false) => {
    setScanning(true)
    setStatus('Scanning…')
    try {
      const result = force ? await api.rescan() : await api.candidates()
      setCandidates(result)
      // Cleared, not set to a count. This used to report result.length — every candidate, including
      // the filtered ones — and because footerStatus prefers `status` over its computed message, it
      // won: the footer claimed "Found 29 candidate(s)" above a list of 16 and the hidden-count
      // hint never rendered. Leaving it empty lets the computed message describe what is on screen.
      setStatus('')
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

  // The native dialog (Windows tray) also writes the candidate cache server-side; on a headless
  // Deck it returns null and the browser opens inside the game's own Proton prefix. Either way the
  // chosen path is persisted with api.candidateFolder and reflected in local state.
  const pickFolderFor = (c: Candidate) => picker.pick({
    name: c.name,
    start: () => c.path || c.prefixPath || null,
    nativePick: () => api.candidateFolderPick(c.id),
    apply: async (path) => {
      await api.candidateFolder(c.id, path)
      setCandidates(prev => prev.map(x => x.id === c.id ? { ...x, path } : x))
    },
  })

  const setSaveFolder = () => {
    const ids = [...checked]
    if (ids.length !== 1) return
    const c = candidates.find(x => x.id === ids[0])
    if (c) void pickFolderFor(c)
  }

  // Enrolling a game with no save folder is what produced the silent Deck failures — it lands a
  // tracked game the archiver cannot back up. Named here so the block is actionable, not just off.
  const missing = [...checked]
    .map(id => candidates.find(c => c.id === id))
    .filter((c): c is Candidate => !!c && !c.path)

  const enroll = async () => {
    if (checked.size === 0 || missing.length > 0) return
    setEnrolling(true)
    setStatus('Enrolling…')
    try {
      const result = await api.enroll([...checked])
      setStatus(
        `Enrolled ${result.enrolled} game(s).` +
        (result.skipped > 0 ? ` Skipped ${result.skipped} already tracked.` : '')
      )
      if (result.enrolled > 0) setEnrolled(true)
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

  // Only chips that would show something are offered — an empty "Heroic" chip on a machine with no
  // Heroic install is a dead end that reads like a bug. Suggested and All always render, so the
  // toolbar never collapses to nothing.
  const chips = useMemo(
    () => FILTERS
      .map(f => ({ ...f, count: candidates.filter(f.match).length }))
      .filter(f => f.count > 0 || f.id === 'suggested' || f.id === 'all'),
    [candidates])

  const stores = useMemo(() => {
    if (filter !== 'heroic') return []
    const heroic = candidates.filter(c => c.source === 'Heroic')
    return STORES
      .map(s => ({ ...s, count: heroic.filter(c => c.store === s.id).length }))
      .filter(s => s.count > 0)
  }, [candidates, filter])

  const active = FILTERS.find(f => f.id === filter) ?? FILTERS[0]
  const visible = candidates
    .filter(active.match)
    .filter(c => !(filter === 'heroic' && store) || c.store === store)

  const enrollBlocked = missing.length > 0
  // Named, not just counted. A user looking for a game they can plainly see in Steam needs to be
  // told it was filtered and by which control — silently showing a shorter list reads as "the scan
  // didn't find it".
  const hiddenCount = candidates.length - visible.length
  const footerStatus = status || (
    enrollBlocked
      ? `Set a save folder for: ${missing.map(c => c.name).join(', ')}`
      : `Showing ${visible.length} of ${candidates.length} game(s) found.` +
        (hiddenCount > 0 ? ' Choose “All” to see every one.' : '')
  )

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
          onClick={() => setSaveFolder()}
        >
          <FolderOpen size={13} strokeWidth={1.75} color="#9CA3AF" />
          <span>Set save folder…</span>
        </button>
      </div>

      {/* Filters */}
      <div style={{ display: 'flex', gap: 5, flexShrink: 0, flexWrap: 'wrap', alignItems: 'center' }}>
        {chips.map(f => (
          <button
            key={f.id}
            title={f.hint}
            onClick={() => { setFilter(f.id); setStore(null) }}
            style={chipStyle(filter === f.id)}
          >
            <span>{f.label}</span>
            <span style={{ opacity: 0.65, fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace", fontSize: 10.5 }}>
              {f.count}
            </span>
          </button>
        ))}
      </div>

      {/* Heroic storefronts — a second axis, shown only while the Heroic filter is on. */}
      {stores.length > 1 && (
        <div style={{ display: 'flex', gap: 5, flexShrink: 0, flexWrap: 'wrap', alignItems: 'center', paddingLeft: 2 }}>
          <span style={{ color: '#6B7280', fontSize: 11 }}>Store:</span>
          <button onClick={() => setStore(null)} style={chipStyle(store === null)}>All</button>
          {stores.map(s => (
            <button key={s.id} onClick={() => setStore(s.id)} style={chipStyle(store === s.id)}>
              <span>{s.label}</span>
              <span style={{ opacity: 0.65, fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace", fontSize: 10.5 }}>
                {s.count}
              </span>
            </button>
          ))}
        </div>
      )}

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
                {/* Only when it adds something the source does not already say. */}
                {c.store && c.store !== 'Unknown' && c.store !== 'Steam' && (
                  <span style={{
                    color: '#9CA3AF', fontSize: 10,
                    background: 'rgba(255,255,255,0.05)',
                    border: '1px solid rgba(255,255,255,0.09)',
                    padding: '1px 6px', borderRadius: 3,
                  }}>
                    {STORES.find(s => s.id === c.store)?.label ?? c.store}
                  </span>
                )}
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
              {c.path ? (
                <div style={{
                  color: '#9CA3AF', fontSize: 10, marginTop: 3,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace",
                }}>
                  {c.path}
                </div>
              ) : (
                // The per-row button is what a Deck user actually hits — the toolbar button needs
                // a tick first, and this appears exactly on the rows that block enrollment.
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 3 }}>
                  <span style={{ color: '#f4a60d', fontSize: 11 }}>No save folder set</span>
                  <button
                    onClick={() => void pickFolderFor(c)}
                    style={{
                      display: 'flex', alignItems: 'center', gap: 5,
                      padding: '4px 9px', background: 'transparent',
                      border: '1px solid #129271', borderRadius: 4,
                      color: '#129271', fontSize: 11, fontWeight: 600,
                      cursor: 'pointer', fontFamily: 'inherit',
                    }}
                  >
                    <FolderSearch size={12} strokeWidth={1.75} />
                    <span>Set save folder</span>
                  </button>
                </div>
              )}
            </div>
          </div>
        ))}
      </div>

      {/* Launch setup appears once a game is enrolled — the "success state" (Linux only; the card
          hides itself when there is no command, i.e. on Windows). */}
      {enrolled && (
        <div style={{ flexShrink: 0, display: 'flex', justifyContent: 'center' }}>
          <LaunchSetupCard />
        </div>
      )}

      {/* Footer */}
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexShrink: 0 }}>
        <span style={{ color: enrollBlocked && !status ? '#f4a60d' : '#9CA3AF', fontSize: 12 }}>
          {footerStatus}
        </span>
        <button
          onClick={() => void enroll()}
          disabled={busy || checked.size === 0 || enrollBlocked}
          style={{
            padding: '7px 18px', background: '#129271', border: 'none', borderRadius: 5,
            color: '#fff', fontSize: 13, fontWeight: 600,
            cursor: checked.size > 0 && !busy && !enrollBlocked ? 'pointer' : 'default',
            fontFamily: 'inherit', letterSpacing: '0.01em',
            opacity: checked.size === 0 || busy || enrollBlocked ? 0.5 : 1,
          }}
        >
          Enroll selected
        </button>
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
