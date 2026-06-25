import { Shield, Server } from 'lucide-react'

interface Props {
  connected: boolean
  serverUrl: string
}

export function StatusHeader({ connected, serverUrl }: Props) {
  const color = connected ? '#129271' : '#f4a60d'
  const label = connected ? 'CONNECTED' : 'DISCONNECTED'
  const display = serverUrl.replace(/^https?:\/\//, '')

  return (
    <div style={{
      padding: '10px 20px', borderBottom: '1px solid #494949',
      background: '#1E252A', display: 'flex', alignItems: 'center',
      justifyContent: 'space-between', flexShrink: 0, minHeight: 54,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 11 }}>
        <Shield size={18} strokeWidth={1.75} color="#129271" />
        <div>
          <div style={{
            color: '#9CA3AF', fontSize: 9, letterSpacing: '0.13em',
            textTransform: 'uppercase', lineHeight: 1, marginBottom: 4,
          }}>
            Agent Status
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <span style={{
              width: 7, height: 7, background: color, borderRadius: '50%',
              boxShadow: `0 0 7px ${color}d9`,
              display: 'inline-block', flexShrink: 0,
            }} />
            <span style={{ color, fontSize: 13, fontWeight: 700, letterSpacing: '0.05em' }}>
              {label}
            </span>
          </div>
        </div>
      </div>

      {display && (
        <div style={{
          display: 'flex', alignItems: 'center', gap: 7,
          padding: '5px 10px',
          background: 'rgba(18,146,113,0.07)',
          border: '1px solid rgba(18,146,113,0.2)',
          borderRadius: 5,
        }}>
          <Server size={13} strokeWidth={1.75} color="#129271" />
          <span style={{
            color: '#9CA3AF', fontSize: 11,
            fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace",
          }}>
            {display}
          </span>
        </div>
      )}
    </div>
  )
}
