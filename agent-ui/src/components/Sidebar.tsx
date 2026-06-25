import { Monitor, Plus, Settings, HardDrive } from 'lucide-react'
import type { View } from '../types'
import logoUrl from '../assets/SaveLocker_Logo_crop.png'

interface Props {
  activeView: View
  onNavigate: (v: View) => void
  machineName: string
}

const NAV: { view: View; label: string; Icon: React.ComponentType<{ size: number; strokeWidth: number; color: string }> }[] = [
  { view: 'overview', label: 'Overview', Icon: Monitor },
  { view: 'addGames', label: 'Add Games', Icon: Plus },
  { view: 'settings', label: 'Settings', Icon: Settings },
]

export function Sidebar({ activeView, onNavigate, machineName }: Props) {
  return (
    <div style={{
      width: 212, minWidth: 212, background: '#1E252A',
      borderRight: '1px solid #494949',
      display: 'flex', flexDirection: 'column', overflow: 'hidden',
    }}>
      {/* Brand */}
      <div style={{
        padding: '15px 14px', borderBottom: '1px solid #494949',
        display: 'flex', alignItems: 'center', gap: 10, flexShrink: 0,
      }}>
        <img
          src={logoUrl}
          alt="SaveLocker"
          style={{ width: 34, height: 34, objectFit: 'contain', borderRadius: 5, flexShrink: 0 }}
        />
        <div>
          <div style={{ color: '#ECEFF1', fontSize: 13, fontWeight: 700, letterSpacing: '-0.015em', lineHeight: 1.2 }}>
            SaveLocker
          </div>
          <div style={{ color: '#9CA3AF', fontSize: 10, letterSpacing: '0.07em', textTransform: 'uppercase', lineHeight: 1.5 }}>
            Agent v1.0
          </div>
        </div>
      </div>

      {/* Navigation */}
      <nav style={{ padding: '10px 8px', flex: 1, display: 'flex', flexDirection: 'column', gap: 2 }}>
        {NAV.map(({ view, label, Icon }) => {
          const active = activeView === view
          return (
            <div
              key={view}
              onClick={() => onNavigate(view)}
              style={{
                display: 'flex', alignItems: 'center', gap: 8,
                padding: '8px 10px', borderRadius: 5, cursor: 'pointer',
                background: active ? 'rgba(18,146,113,0.14)' : 'transparent',
                color: active ? '#129271' : '#ECEFF1',
                fontSize: 13, fontWeight: active ? 600 : 400,
                borderLeft: `2px solid ${active ? '#129271' : 'transparent'}`,
                transition: 'background 0.12s ease', userSelect: 'none',
              }}
            >
              <Icon size={14} strokeWidth={1.75} color={active ? '#129271' : '#9CA3AF'} />
              <span>{label}</span>
            </div>
          )
        })}
      </nav>

      {/* Machine footer */}
      <div style={{
        padding: '11px 14px', borderTop: '1px solid #494949',
        flexShrink: 0, display: 'flex', alignItems: 'center', gap: 7,
      }}>
        <HardDrive size={12} strokeWidth={1.75} color="#9CA3AF" />
        <span style={{ color: '#9CA3AF', fontSize: 11 }}>Machine: {machineName}</span>
      </div>
    </div>
  )
}
