import { useState, useEffect, useCallback, useMemo } from 'react'
import { RefreshCw, FolderOpen, FolderSearch } from 'lucide-react'
import type { Candidate, DuplicateShortcut, LinkOption, SaveFolder } from '../types'
import { api } from '../api'
import { useFolderPicker } from '../useFolderPicker'
import { PathBrowserModal } from './PathBrowserModal'
import { LaunchSetupCard } from './LaunchSetupCard'
import { ServerGameLink, type LinkPick } from './ServerGameLink'

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
type FilterId = 'suggested' | 'all' | 'steam' | 'shortcut' | 'heroic' | 'emulator' | 'nopath'

const FILTERS: { id: FilterId; label: string; hint: string; match: (c: Candidate) => boolean }[] = [
  { id: 'suggested', label: 'Suggested', hint: 'Everything except games Steam Cloud already backs up', match: c => !c.hasSteamCloud },
  { id: 'all', label: 'All', hint: 'Every game found, including Steam Cloud titles', match: () => true },
  { id: 'steam', label: 'Steam', hint: 'Games installed from the Steam store', match: c => c.source === 'SteamInstalled' },
  { id: 'shortcut', label: 'Added to Steam', hint: 'Non-Steam games you added to your Steam library', match: c => c.source === 'SteamShortcut' },
  { id: 'heroic', label: 'Heroic', hint: 'Games installed through Heroic Games Launcher', match: c => c.source === 'Heroic' },
  { id: 'emulator', label: 'Emulators', hint: 'Games found by their save files in an emulator’s saves folder', match: c => c.source === 'Emulator' },
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

/** The emulators SaveLocker reads saves from, as a second axis under the Emulators filter. `id` is the
 * agent's `emulatorName`. */
const EMULATORS: { id: string; label: string }[] = [
  { id: 'RetroArch', label: 'RetroArch' },
  { id: 'melonDS', label: 'melonDS' },
  { id: 'Supermodel', label: 'Supermodel' },
  { id: 'Model 2', label: 'Model 2' },
  { id: 'ScummVM', label: 'ScummVM' },
  { id: 'PCSX2', label: 'PCSX2' },
  { id: 'DuckStation', label: 'DuckStation' },
  { id: 'Dolphin', label: 'Dolphin' },
  { id: 'PrimeHack', label: 'PrimeHack' },
  { id: 'PPSSPP', label: 'PPSSPP' },
  { id: 'RPCS3', label: 'RPCS3' },
  { id: 'Vita3K', label: 'Vita3K' },
  { id: 'shadPS4', label: 'shadPS4' },
]

/** The Console row's chips: one per EmuDeck system among `found` ("ps2"), labelled as the agent says it ("PS2"),
 * in label order. Rows with no system (an arcade set with no ROM folder) are under All only. */
function consoleChips(found: Candidate[]): { id: string; label: string; count: number }[] {
  const bySystem = new Map<string, { id: string; label: string; count: number }>()
  for (const c of found) {
    if (!c.emulatorSystem) continue
    const chip = bySystem.get(c.emulatorSystem) ?? { id: c.emulatorSystem, label: c.emulatorConsole ?? c.emulatorSystem, count: 0 }
    chip.count++
    bySystem.set(c.emulatorSystem, chip)
  }
  return [...bySystem.values()].sort((a, b) => a.label.localeCompare(b.label))
}

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
  // "Also found" folders the user unticked, per candidate id. Every offered folder is ticked by default.
  const [unticked, setUnticked] = useState<Map<number, Set<string>>>(new Map())
  const [filter, setFilter] = useState<FilterId>('suggested')
  const [store, setStore] = useState<string | null>(null)
  const [emulator, setEmulatorState] = useState<string | null>(null)
  // The console (an EmuDeck system folder name, "ps2"), a third axis under the emulator. Picking another
  // emulator clears it: the console picked may have no games under the new one.
  const [consoleId, setConsoleId] = useState<string | null>(null)
  const setEmulator = (id: string | null) => { setEmulatorState(id); setConsoleId(null) }
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

  // Which server game each emulator save joins, asked once per list. A fresh scan renumbers the
  // candidates, so every hand-made choice is dropped with it.
  const [links, setLinks] = useState<Map<number, LinkOption[]>>(new Map())
  // False when the agent could not ask the server: the rows say the link is decided when the game is added.
  const [linksReachable, setLinksReachable] = useState(true)
  const [linkPicks, setLinkPicks] = useState<Map<number, LinkPick>>(new Map())
  const [linkOpen, setLinkOpen] = useState<number | null>(null)
  useEffect(() => {
    setLinkPicks(new Map())
    setLinkOpen(null)
    setLinksReachable(true)
    if (!candidates.some(c => c.source === 'Emulator' && !c.enrolled)) { setLinks(new Map()); return }
    let live = true
    api.candidateLinks()
      .then(r => {
        if (!live) return
        setLinks(new Map(r.links.map(l => [l.id, l.options])))
        setLinksReachable(r.reachable)
      })
      .catch(() => { if (live) { setLinks(new Map()); setLinksReachable(false) } })
    return () => { live = false }
  }, [candidates])
  // EmuDeck's preinstalled saves, never played here, are not the user's games: one is listed only when a
  // server game keeps it, so this machine can take the fleet's save in its place.
  const listed = useMemo(
    () => candidates.filter(c => !c.untouchedSeed || c.enrolled || links.get(c.id)?.[0]?.kind === 'join'),
    [candidates, links])
  const pickLink = (id: number, pick: LinkPick | undefined) => {
    setLinkPicks(prev => {
      const next = new Map(prev)
      if (pick) next.set(id, pick)
      else next.delete(id)
      return next
    })
    setLinkOpen(null)
  }

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
      const alsoSync = [...checked]
        .map(id => candidates.find(c => c.id === id))
        .filter((c): c is Candidate => !!c && !!c.alsoFound?.length)
        .map(c => ({ id: c.id, paths: c.alsoFound!.map(f => f.path).filter(p => !unticked.get(c.id)?.has(p)) }))
        .filter(choice => choice.paths.length > 0)
      const linked = [...checked].flatMap(id => {
        const pick = linkPicks.get(id)
        return pick ? [{ id, choice: pick.choice, gameId: pick.gameId ?? null }] : []
      })
      const result = await api.enroll([...checked], alsoSync, linked)
      setStatus(
        `Enrolled ${result.enrolled} game(s).` +
        (result.skipped > (result.notes?.length ?? 0) ? ` Skipped ${result.skipped - (result.notes?.length ?? 0)} already tracked.` : '') +
        (result.notes?.length ? ` Not added: ${result.notes.join(' ')}` : '')
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
      .map(f => ({ ...f, count: listed.filter(f.match).length }))
      .filter(f => f.count > 0 || f.id === 'suggested' || f.id === 'all'),
    [listed])

  const stores = useMemo(() => {
    if (filter !== 'heroic') return []
    const heroic = listed.filter(c => c.source === 'Heroic')
    return STORES
      .map(s => ({ ...s, count: heroic.filter(c => c.store === s.id).length }))
      .filter(s => s.count > 0)
  }, [listed, filter])

  // Shown even with one emulator: it says which ones SaveLocker reads saves from.
  const emulators = useMemo(() => {
    if (filter !== 'emulator') return []
    const emulated = listed.filter(c => c.source === 'Emulator')
    return EMULATORS.map(e => ({ ...e, count: emulated.filter(c => c.emulatorName === e.id).length }))
  }, [listed, filter])

  // Only offered when the games found span more than one console.
  const consoles = useMemo(() => {
    if (filter !== 'emulator') return []
    return consoleChips(listed.filter(c => c.source === 'Emulator' && (!emulator || c.emulatorName === emulator)))
  }, [listed, filter, emulator])
  // The pick applies only while its chip is on screen: a filter nobody can see or clear would hide rows.
  const activeConsole = consoles.length > 1 && consoles.some(s => s.id === consoleId) ? consoleId : null

  const active = FILTERS.find(f => f.id === filter) ?? FILTERS[0]
  const visible = listed
    .filter(active.match)
    .filter(c => !(filter === 'heroic' && store) || c.store === store)
    .filter(c => !(filter === 'emulator' && emulator) || c.emulatorName === emulator)
    .filter(c => !(filter === 'emulator' && activeConsole) || c.emulatorSystem === activeConsole)

  const enrollBlocked = missing.length > 0
  // Named, not just counted. A user looking for a game they can plainly see in Steam needs to be
  // told it was filtered and by which control — silently showing a shorter list reads as "the scan
  // didn't find it".
  const hiddenCount = listed.length - visible.length
  const footerStatus = status || (
    enrollBlocked
      ? `Set a save folder for: ${missing.map(c => c.name).join(', ')}`
      : `Showing ${visible.length} of ${listed.length} game(s) found.` +
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

      {emulators.length > 0 && (
        <div style={{ display: 'flex', gap: 5, flexShrink: 0, flexWrap: 'wrap', alignItems: 'center', paddingLeft: 2 }}>
          <span style={{ color: '#6B7280', fontSize: 11 }}>Emulator:</span>
          <button onClick={() => setEmulator(null)} style={chipStyle(emulator === null)}>All</button>
          {emulators.map(e => (
            <button key={e.id} onClick={() => setEmulator(e.id)} style={chipStyle(emulator === e.id)}>
              <span>{e.label}</span>
              <span style={{ opacity: 0.65, fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace", fontSize: 10.5 }}>
                {e.count}
              </span>
            </button>
          ))}
        </div>
      )}

      {consoles.length > 1 && (
        <div style={{ display: 'flex', gap: 5, flexShrink: 0, flexWrap: 'wrap', alignItems: 'center', paddingLeft: 2 }}>
          <span style={{ color: '#6B7280', fontSize: 11 }}>Console:</span>
          <button onClick={() => setConsoleId(null)} style={chipStyle(activeConsole === null)}>All</button>
          {consoles.map(s => (
            <button key={s.id} onClick={() => setConsoleId(s.id)} style={chipStyle(activeConsole === s.id)}>
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
              disabled={!!c.notSyncable}
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
                {/* The save file's own name too: two ROMs (two regions, two consoles) can share a title. */}
                {[c.emulatorName, c.region, c.emulatorRom && c.emulatorRom !== c.name ? c.emulatorRom : null].filter(Boolean).map(t => (
                  <span key={t} style={{
                    color: '#9CA3AF', fontSize: 10,
                    background: 'rgba(255,255,255,0.05)',
                    border: '1px solid rgba(255,255,255,0.09)',
                    padding: '1px 6px', borderRadius: 3,
                  }}>
                    {t}
                  </span>
                ))}
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
                {c.duplicateShortcuts && c.duplicateShortcuts.length > 0 && (
                  <span
                    title={c.duplicateShortcuts
                      .map(d => `${d.name} — AppID ${d.appId ?? 'none'}${d.tracked ? ' (will be tracked)' : ''}`)
                      .join('\n')}
                    style={{
                      color: '#f4a60d', fontSize: 10,
                      background: 'rgba(244,166,13,0.12)',
                      border: '1px solid rgba(244,166,13,0.45)',
                      padding: '1px 6px', borderRadius: 3, cursor: 'help',
                    }}
                  >
                    {c.duplicateShortcuts.length} duplicate AppIDs
                  </span>
                )}
              </div>
              {c.duplicateShortcuts && c.duplicateShortcuts.length > 0 && (
                <DuplicateNote shortcuts={c.duplicateShortcuts} />
              )}
              {!c.enrolled && c.statesOnly && (
                <div style={{ color: '#9CA3AF', fontSize: 11, marginTop: 4 }}>
                  Save states only so far. Its in-game saves sync too once the game makes one.
                </div>
              )}
              {!c.enrolled && c.untouchedSeed && (
                <div style={{ color: '#9CA3AF', fontSize: 11, marginTop: 4 }}>
                  EmuDeck’s preinstalled save, never played here. Adding it takes the server’s save in its place.
                </div>
              )}
              {!c.enrolled && c.source === 'Emulator' && !c.notSyncable && !linksReachable && (
                <div style={{ color: '#9CA3AF', fontSize: 11, marginTop: 4 }}>
                  The server can’t be reached, so which server game this joins is decided when you add it.
                </div>
              )}
              {!c.enrolled && (links.get(c.id)?.length ?? 0) > 0 && (
                <ServerGameLink
                  title={c.name}
                  options={links.get(c.id)!}
                  pick={linkPicks.get(c.id)}
                  open={linkOpen === c.id}
                  onToggle={() => setLinkOpen(linkOpen === c.id ? null : c.id)}
                  onPick={pick => pickLink(c.id, pick)}
                />
              )}
              {!c.enrolled && c.alsoFound && c.alsoFound.length > 0 && (
                <AlsoFound
                  folders={c.alsoFound}
                  unticked={unticked.get(c.id) ?? new Set()}
                  onToggle={path => setUnticked(prev => {
                    const next = new Map(prev)
                    const set = new Set(next.get(c.id) ?? [])
                    if (set.has(path)) set.delete(path); else set.add(path)
                    next.set(c.id, set)
                    return next
                  })}
                />
              )}
              {c.notSyncable ? (
                // A memory card every game of a console shares: never a game, so no folder to change. The row
                // says why those saves are not offered, and what to switch in the emulator.
                <div style={{ marginTop: 3 }}>
                  {c.path && (
                    <div style={{ color: '#9CA3AF', fontSize: 10, fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace" }}>{c.path}</div>
                  )}
                  <div style={{ color: '#f4a60d', fontSize: 11, marginTop: 2 }}>{c.notSyncable}</div>
                </div>
              ) : c.path ? (
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

// Steam has this game under several AppIDs and only ever launches one: name the one this scan follows.
function DuplicateNote({ shortcuts }: { shortcuts: DuplicateShortcut[] }) {
  const tracked = shortcuts.find(s => s.tracked)
  return (
    <div style={{ color: '#f4a60d', fontSize: 11, marginTop: 3, lineHeight: 1.4 }}>
      {tracked
        ? <>Steam launches only one of these shortcuts. SaveLocker will track AppID {tracked.appId ?? 'none'} —
            if that is the stale one, its saves are never backed up. Remove the stale shortcut in Steam, or
            set the save folder by hand.</>
        : <>Steam launches only one of these shortcuts, but SaveLocker found this game&apos;s saves another way,
            so none of them is followed.</>}
    </div>
  )
}

// The manifest's other locations for this game that exist here. Ticked ones are added as extra save folders
// on enroll; nothing is adopted unasked, since a second location may be an alternative install, not a companion.
function AlsoFound({ folders, unticked, onToggle }: {
  folders: SaveFolder[]
  unticked: Set<string>
  onToggle: (path: string) => void
}) {
  return (
    <div style={{ marginTop: 4 }} onClick={e => e.stopPropagation()}>
      <div style={{ color: '#9CA3AF', fontSize: 11 }}>Also found — sync these folders too:</div>
      {folders.map(f => (
        <label key={f.path} style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 11, color: '#ECEFF1', marginTop: 2 }}>
          <input type="checkbox" checked={!unticked.has(f.path)} onChange={() => onToggle(f.path)} />
          <span style={{
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace", fontSize: 10, color: '#9CA3AF',
          }} title={f.path}>{f.path}</span>
        </label>
      ))}
    </div>
  )
}
