import { useEffect, useRef, useState } from 'react'
import { Activity as ActivityIcon, RefreshCw } from 'lucide-react'
import { api } from '../api'
import type { Activity, SyncActivitySnapshot } from '../types'

/**
 * The bottom of the Overview page: what is syncing right now (with a progress bar for a push —
 * the direction slow enough to want one, since a save large enough to take a while is exactly what
 * the chunked upload protocol exists for), a "Sync now" button, and a short rolling log of what just
 * happened. Everything here comes from /api/activity, an in-memory read on the agent's side, so
 * polling it every couple of seconds while this card is mounted costs nothing.
 */
export function ActivityCard() {
  const [activity, setActivity] = useState<Activity | null>(null)
  const [syncing, setSyncing] = useState(false)
  const [syncMessage, setSyncMessage] = useState<string | null>(null)
  const logRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let cancelled = false
    const poll = () => api.activity().then(a => { if (!cancelled) setActivity(a) }).catch(() => {})
    poll()
    const id = setInterval(poll, 1500)
    return () => { cancelled = true; clearInterval(id) }
  }, [])

  async function syncNow() {
    setSyncing(true)
    setSyncMessage(null)
    try {
      const { message } = await api.syncNow()
      setSyncMessage(message)
    } catch (err) {
      setSyncMessage(err instanceof Error ? err.message : 'Sync failed.')
    } finally {
      setSyncing(false)
    }
  }

  const current = activity?.current
  const recent = activity?.recent ?? []

  return (
    <div style={{
      background: '#1E252A', border: '1px solid #494949', borderRadius: 8,
      padding: '12px 16px', display: 'flex', flexDirection: 'column', gap: 10,
      textAlign: 'left', width: '100%', maxWidth: 560,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <ActivityIcon size={16} strokeWidth={1.9} color="#129271" style={{ flexShrink: 0 }} />
        <span style={{ color: '#ECEFF1', fontSize: 13, fontWeight: 700 }}>Activity</span>
        <button
          onClick={() => void syncNow()}
          disabled={syncing}
          style={{
            marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 5,
            padding: '4px 11px', background: 'transparent',
            border: '1px solid #494949', borderRadius: 5,
            color: syncing ? '#9CA3AF' : '#ECEFF1', fontSize: 12, fontWeight: 600,
            cursor: syncing ? 'default' : 'pointer', fontFamily: 'inherit',
          }}
        >
          <RefreshCw size={12} strokeWidth={2} style={syncing ? { animation: 'sl-spin 1s linear infinite' } : undefined} />
          {syncing ? 'Syncing…' : 'Sync now'}
        </button>
      </div>

      <CurrentStatus current={current} />
      {syncMessage && !syncing && (
        <div style={{ color: '#9CA3AF', fontSize: 11.5, lineHeight: 1.4 }}>{syncMessage}</div>
      )}

      <div style={{ borderTop: '1px solid #33393f' }} />

      <div
        ref={logRef}
        style={{
          display: 'flex', flexDirection: 'column', gap: 4,
          maxHeight: 150, overflowY: 'auto',
        }}
      >
        {recent.length === 0 ? (
          <div style={{ color: '#6B7280', fontSize: 12, padding: '4px 0' }}>No activity yet.</div>
        ) : (
          recent.map((e, i) => <LogRow key={`${e.timestampUtc}-${i}`} timestampUtc={e.timestampUtc} message={e.message} />)
        )}
      </div>

      {/* Scoped keyframes for the sync-now spinner — no global stylesheet to hook into here. */}
      <style>{'@keyframes sl-spin { to { transform: rotate(360deg) } }'}</style>
    </div>
  )
}

function CurrentStatus({ current }: { current?: SyncActivitySnapshot }) {
  if (!current || current.phase === 'Idle') {
    return (
      <div style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
        <span style={{ width: 7, height: 7, borderRadius: '50%', background: '#494949', flexShrink: 0 }} />
        <span style={{ color: '#9CA3AF', fontSize: 12.5 }}>Idle — nothing syncing right now.</span>
      </div>
    )
  }

  const verb = current.phase === 'Pushing' ? 'Pushing' : current.phase === 'Pulling' ? 'Pulling' : 'Settling'
  const showBar = current.phase === 'Pushing' && current.bytesTotal > 0
  const pct = showBar ? Math.min(100, Math.round((current.bytesDone / current.bytesTotal) * 100)) : 0

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
        <span style={{
          width: 7, height: 7, borderRadius: '50%', background: '#129271', flexShrink: 0,
          animation: 'sl-pulse 1.4s ease-in-out infinite',
        }} />
        <span style={{ color: '#ECEFF1', fontSize: 12.5 }}>
          {verb} <strong>{current.gameName}</strong>…
        </span>
        {showBar && (
          <span style={{ marginLeft: 'auto', color: '#9CA3AF', fontSize: 11, fontVariantNumeric: 'tabular-nums' }}>
            {formatBytes(current.bytesDone)} / {formatBytes(current.bytesTotal)}
          </span>
        )}
      </div>
      {showBar && (
        <div style={{ height: 5, background: '#12181C', borderRadius: 3, overflow: 'hidden' }}>
          <div style={{
            width: `${pct}%`, height: '100%', background: '#129271',
            transition: 'width 0.3s ease',
          }} />
        </div>
      )}
      <style>{'@keyframes sl-pulse { 0%, 100% { opacity: 1 } 50% { opacity: 0.35 } }'}</style>
    </div>
  )
}

function LogRow({ timestampUtc, message }: { timestampUtc: string; message: string }) {
  const warn = /conflict|refused|failed|unreachable|blocked|error/i.test(message)
  return (
    <div style={{ display: 'flex', gap: 8, alignItems: 'baseline' }}>
      <span style={{
        color: '#6B7280', fontSize: 10.5, fontVariantNumeric: 'tabular-nums',
        flexShrink: 0, width: 58,
        fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace",
      }}>
        {formatTime(timestampUtc)}
      </span>
      <span style={{ color: warn ? '#f4a60d' : '#9CA3AF', fontSize: 11.5, lineHeight: 1.5 }}>
        {message}
      </span>
    </div>
  )
}

function formatTime(iso: string): string {
  const normalized = /[Z+]/.test(iso.slice(-6)) ? iso : iso + 'Z'
  const d = new Date(normalized)
  return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })
}

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / (1024 * 1024)).toFixed(1)} MB`
}
