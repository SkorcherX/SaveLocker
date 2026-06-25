import { Cpu } from 'lucide-react'
import type { AgentState } from '../types'

interface Props {
  state: AgentState | null
}

export function OverviewView({ state }: Props) {
  return (
    <div style={{
      position: 'absolute', inset: 0,
      display: 'flex', flexDirection: 'column',
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
