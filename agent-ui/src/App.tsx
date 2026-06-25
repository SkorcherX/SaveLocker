import { useEffect, useState, useCallback } from 'react'
import type { View, AgentState } from './types'
import { api } from './api'
import { Sidebar } from './components/Sidebar'
import { StatusHeader } from './components/StatusHeader'
import { OverviewView } from './components/OverviewView'
import { AddGamesView } from './components/AddGamesView'
import { SettingsView } from './components/SettingsView'

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
        borderRadius: 9,
        overflow: 'hidden',
        boxShadow: '0 28px 70px rgba(0,0,0,0.8), 0 0 0 1px rgba(255,255,255,0.05)',
        flexShrink: 0,
      }}>
        <Sidebar
          activeView={view}
          onNavigate={setView}
          machineName={state?.machineName ?? '…'}
        />

        <div style={{ flex: 1, minWidth: 0, background: '#2A3238', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
          <StatusHeader
            connected={state?.connected ?? false}
            serverUrl={state?.serverUrl ?? ''}
          />
          <div style={{ flex: 1, position: 'relative', overflow: 'hidden' }}>
            {view === 'overview' && <OverviewView state={state} />}
            {view === 'addGames' && <AddGamesView onEnrolled={refreshState} />}
            {view === 'settings' && <SettingsView state={state} onSaved={refreshState} />}
          </div>
        </div>
      </div>
    </div>
  )
}
