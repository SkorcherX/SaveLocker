import { Cpu, AlertTriangle } from 'lucide-react'
import type { AgentState, LeaseWarning } from '../types'
import { api } from '../api'

interface Props {
  state: AgentState | null
  onWarningDismissed: () => void
}

export function OverviewView({ state, onWarningDismissed }: Props) {
  const warnings = state?.leaseWarnings ?? []

  async function dismiss(w: LeaseWarning) {
    try { await api.dismissLeaseWarning(w.gameName) } catch { /* ignore */ }
    onWarningDismissed()
  }

  return (
    <div style={{
      position: 'absolute', inset: 0,
      display: 'flex', flexDirection: 'column',
      overflow: 'hidden',
    }}>
      {/* Lease conflict banners */}
      {warnings.length > 0 && (
        <div style={{ flexShrink: 0, padding: '10px 16px', display: 'flex', flexDirection: 'column', gap: 8 }}>
          {warnings.map(w => (
            <div key={w.gameName} style={{
              display: 'flex', alignItems: 'flex-start', gap: 10,
              background: 'rgba(244,166,13,0.12)', border: '1px solid rgba(244,166,13,0.45)',
              borderRadius: 7, padding: '10px 14px',
            }}>
              <AlertTriangle size={16} strokeWidth={2} color="#f4a60d" style={{ flexShrink: 0, marginTop: 1 }} />
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ color: '#f4a60d', fontSize: 13, fontWeight: 600, lineHeight: 1.3 }}>
                  Save conflict risk — {w.gameName}
                </div>
                <div style={{ color: '#ECEFF1', fontSize: 12, marginTop: 3, lineHeight: 1.5 }}>
                  <strong style={{ color: '#f4a60d' }}>{w.holderMachine}</strong> already has this game
                  checked out. You launched without pulling their latest save — a conflict will likely
                  appear in the dashboard when you exit.
                </div>
              </div>
              <button
                onClick={() => dismiss(w)}
                style={{
                  flexShrink: 0, background: 'transparent', border: 'none',
                  color: '#9CA3AF', fontSize: 18, lineHeight: 1, cursor: 'pointer',
                  padding: '0 2px', marginTop: -1,
                }}
                title="Dismiss"
              >×</button>
            </div>
          ))}
        </div>
      )}

      {/* Main content */}
      <div style={{
        flex: 1, display: 'flex', flexDirection: 'column',
        alignItems: 'center', justifyContent: 'center',
        gap: 18, padding: 24,
      }}>
        <Cpu size={40} strokeWidth={1.75} color="#129271" />
        <div style={{ textAlign: 'center' }}>
          <div style={{ color: '#ECEFF1', fontSize: 18, fontWeight: 700, letterSpacing: '-0.02em' }}>
            Agent Running
          </div>
          <div style={{ color: '#9CA3AF', fontSize: 13, marginTop: 5, lineHeight: 1.5 }}>
            Monitoring save game activity on {state?.machineName ?? '…'}
          </div>
        </div>
        <div style={{ display: 'flex', gap: 14, marginTop: 4 }}>
          <StatCard value={String(state?.gamesTracked ?? '…')} label="Games Tracked" color="#129271" />
          <StatCard value={String(state?.savesBacked ?? '…')} label="Saves Backed Up" color="#ECEFF1" />
          <StatCard value={state?.lastSyncAgo ?? '—'} label="Last Sync" color="#9CA3AF" />
        </div>
      </div>
    </div>
  )
}

function StatCard({ value, label, color }: { value: string; label: string; color: string }) {
  return (
    <div style={{
      background: '#1E252A', border: '1px solid #494949', borderRadius: 7,
      padding: '14px 22px', textAlign: 'center', minWidth: 112,
    }}>
      <div style={{
        color, fontSize: 28, fontWeight: 700,
        fontVariantNumeric: 'tabular-nums', lineHeight: 1,
      }}>
        {value}
      </div>
      <div style={{ color: '#9CA3AF', fontSize: 11, marginTop: 5, letterSpacing: '0.02em' }}>
        {label}
      </div>
    </div>
  )
}
