import { useEffect, useState, useCallback } from 'react';
import { api } from '../api';
import type { AuditEntry } from '../types';

const ACTION_COLORS: Record<string, string> = {
  'upload.create':     '#129271',
  'upload.force':      '#129271',
  'upload.conflict':   '#f4a60d',
  'conflict.resolve':  '#129271',
  'lease.acquire':     '#4a9eff',
  'lease.release':     '#4a9eff',
  'lease.force_release': '#f4a60d',
  'game.create':       '#129271',
  'game.delete':       '#e05252',
  'game.enable':       '#129271',
  'game.disable':      '#9CA3AF',
  'game.save_dir':     '#9CA3AF',
  'machine.register':  '#129271',
  'machine.reregister':'#4a9eff',
  'machine.delete':    '#e05252',
  'machine_path.set':  '#9CA3AF',
  'command.enqueue':   '#4a9eff',
  'command.complete':  '#129271',
  'enrollment.create': '#4a9eff',
  'enrollment.redeem': '#129271',
  'enrollment.revoke': '#e05252',
  'enrollment.expire': '#9CA3AF',
};

function ActionBadge({ action }: { action: string }) {
  const color = ACTION_COLORS[action] ?? '#9CA3AF';
  return (
    <span style={{
      display: 'inline-block',
      padding: '2px 7px',
      borderRadius: 4,
      fontSize: 11,
      fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace",
      background: color + '22',
      color,
      border: `1px solid ${color}44`,
      whiteSpace: 'nowrap',
    }}>
      {action}
    </span>
  );
}

function formatTs(iso: string) {
  const normalized = /[Z+]/.test(iso.slice(-6)) ? iso : iso + 'Z';
  const d = new Date(normalized);
  return d.toLocaleString(undefined, {
    month: 'short', day: 'numeric',
    hour: '2-digit', minute: '2-digit', second: '2-digit',
  });
}

export function AuditView() {
  const [entries, setEntries] = useState<AuditEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      setEntries(await api.audit());
    } catch (e) {
      setError('Failed to load audit log: ' + (e as Error).message);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  return (
    <div style={{ padding: '20px 24px', flex: 1, minHeight: 0, overflowY: 'auto' }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 16 }}>
        <span style={{ color: '#129271', fontSize: 10, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.12em' }}>
          Audit Log — last {entries.length} events
        </span>
        <button
          onClick={load}
          style={{
            padding: '5px 13px', background: 'transparent', border: '1px solid #494949',
            borderRadius: 4, color: '#ECEFF1', fontSize: 12, cursor: 'pointer', fontFamily: 'inherit',
          }}
        >
          ↻ Refresh
        </button>
      </div>

      {error && <div style={{ color: '#e05252', fontSize: 13, marginBottom: 12 }}>{error}</div>}
      {loading && entries.length === 0 && (
        <div style={{ color: '#556070', fontSize: 13 }}>Loading…</div>
      )}

      {entries.length > 0 && (
        <div style={{ overflowX: 'auto' }}>
          <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
            <thead>
              <tr style={{ borderBottom: '1px solid #494949' }}>
                {['Time', 'Machine', 'Game', 'Action', 'Detail'].map(h => (
                  <th key={h} style={{
                    padding: '6px 10px', textAlign: 'left',
                    color: '#9CA3AF', fontSize: 10, textTransform: 'uppercase',
                    letterSpacing: '0.09em', fontWeight: 600, whiteSpace: 'nowrap',
                  }}>{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {entries.map((e, i) => (
                <tr
                  key={e.id}
                  style={{
                    borderBottom: '1px solid rgba(73,73,73,0.35)',
                    background: i % 2 === 0 ? 'transparent' : 'rgba(255,255,255,0.015)',
                  }}
                >
                  <td style={{ padding: '7px 10px', color: '#9CA3AF', whiteSpace: 'nowrap', fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace", fontSize: 11 }}>
                    {formatTs(e.timestamp)}
                  </td>
                  <td style={{ padding: '7px 10px', color: '#ECEFF1', whiteSpace: 'nowrap' }}>
                    {e.machineName ?? <span style={{ color: '#494949' }}>—</span>}
                  </td>
                  <td style={{ padding: '7px 10px', color: '#ECEFF1', whiteSpace: 'nowrap' }}>
                    {e.gameName ?? <span style={{ color: '#494949' }}>—</span>}
                  </td>
                  <td style={{ padding: '7px 10px' }}>
                    <ActionBadge action={e.action} />
                  </td>
                  <td style={{
                    padding: '7px 10px', color: '#9CA3AF',
                    fontFamily: "ui-monospace, 'Cascadia Code', Consolas, monospace",
                    fontSize: 11, maxWidth: 380,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  }}>
                    {e.detail ?? ''}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {!loading && entries.length === 0 && !error && (
        <div style={{ color: '#556070', fontSize: 13 }}>No audit events yet.</div>
      )}
    </div>
  );
}
