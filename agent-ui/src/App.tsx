import { useEffect, useState, useCallback } from 'react'
import { HardDrive } from 'lucide-react'
import type { View, AgentState } from './types'
import { api } from './api'
import { Sidebar } from './components/Sidebar'
import { StatusHeader } from './components/StatusHeader'
import { OverviewView } from './components/OverviewView'
import { AddGamesView } from './components/AddGamesView'
import { SettingsView } from './components/SettingsView'
import logoUrl from './assets/SaveLocker_Logo_crop.png'

export default function App() {
  const [view, setView] = useState<View>('addGames')
  const [state, setState] = useState<AgentState | null>(null)

  const refreshState = useCallback(() => {
    api.state().then(setState).catch(console.error)
  }, [])

  useEffect(() => {
    refreshState()
    const id = setInterval(refreshState, 10_000)
    return () => clearInterval(id)
  }, [refreshState])

  return (
    <div style={{
      minHeight: '100vh',
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'center',
      background: '#0d1114',
      padding: 30,
      fontFamily: "'Inter', system-ui, -apple-system, sans-serif",
    }}>
      {/* App window */}
      <div style={{
        width: 900,
        height: 600,
        display: 'flex',
        flexDirection: 'column',
        borderRadius: 9,
        overflow: 'hidden',
        boxShadow: '0 28px 70px rgba(0,0,0,0.8), 0 0 0 1px rgba(255,255,255,0.05)',
        flexShrink: 0,
      }}>
        {/* Shared header row — one element, guaranteed alignment */}
        <div style={{ display: 'flex', borderBottom: '1px solid #494949', flexShrink: 0 }}>
          <div style={{
            width: 212, minWidth: 212, padding: '15px 14px',
            background: '#1E252A', borderRight: '1px solid #494949',
            display: 'flex', alignItems: 'center', gap: 10,
          }}>
            <img src={logoUrl} alt="SaveLocker" style={{ width: 34, height: 34, objectFit: 'contain', borderRadius: 5, flexShrink: 0 }} />
            <div>
              <div style={{ color: '#ECEFF1', fontSize: 13, fontWeight: 700, letterSpacing: '-0.015em', lineHeight: 1.2 }}>SaveLocker</div>
              <div style={{ color: '#9CA3AF', fontSize: 10, letterSpacing: '0.07em', textTransform: 'uppercase', lineHeight: 1.5 }}>Agent v1.0</div>
            </div>
          </div>
          <StatusHeader connected={state?.connected ?? false} serverUrl={state?.serverUrl ?? ''} />
        </div>

        {/* Main row: sidebar nav + content */}
        <div style={{ flex: 1, display: 'flex', overflow: 'hidden' }}>
          <Sidebar activeView={view} onNavigate={setView} />

          <div style={{ flex: 1, minWidth: 0, background: '#2A3238', position: 'relative', overflow: 'hidden' }}>
            {view === 'overview' && <OverviewView state={state} />}
            {view === 'addGames' && <AddGamesView onEnrolled={refreshState} />}
            {view === 'settings' && <SettingsView state={state} onSaved={refreshState} />}
          </div>
        </div>

        {/* Shared footer row — one element, guaranteed alignment */}
        <div style={{ display: 'flex', borderTop: '1px solid #494949', flexShrink: 0 }}>
          <div style={{
            width: 212, minWidth: 212, padding: '11px 14px',
            background: '#1E252A', borderRight: '1px solid #494949',
            display: 'flex', alignItems: 'center', gap: 7,
          }}>
            <HardDrive size={12} strokeWidth={1.75} color="#9CA3AF" />
            <span style={{ color: '#9CA3AF', fontSize: 11 }}>Machine: {state?.machineName ?? '…'}</span>
          </div>
          <div style={{ flex: 1, background: '#2A3238' }} />
        </div>
      </div>
    </div>
  )
}
