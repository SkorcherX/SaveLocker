import { useState } from 'react';
import { api } from '../api';
import type { Machine, Settings } from '../types';
import logoUrl from '../assets/SaveLocker_Logo_crop.png';

interface Props {
  machines: Machine[];
  settings: Settings;
  onRefresh: () => void;
}

const when = (t: string | null | undefined) => t ? new Date(t).toLocaleString() : '—';

export function ConfigView({ machines, settings, onRefresh }: Props) {
  const [sgdbInput, setSgdbInput] = useState('');

  async function handleSaveKey() {
    const v = sgdbInput.trim();
    if (!v) { alert('Paste a SteamGridDB API key first.'); return; }
    try {
      const res = await api.saveSgdbKey(v);
      setSgdbInput('');
      alert(res.message || 'Saved.');
      onRefresh();
    } catch (e) { alert('Could not save key: ' + (e as Error).message); }
  }

  async function handleClearKey() {
    if (!confirm('Clear the SteamGridDB API key? Artwork refresh will stop working until a key is set.')) return;
    try { await api.saveSgdbKey(null); onRefresh(); } catch (e) { alert('Could not clear key: ' + (e as Error).message); }
  }

  async function handleDeleteMachine(machineId: string, name: string) {
    if (!confirm(`Delete machine "${name}"? Its API key stops working immediately. Saved versions it uploaded are kept as history.`)) return;
    try { await api.deleteMachine(machineId); onRefresh(); } catch (e) { alert('Delete machine failed: ' + (e as Error).message); }
  }

  const card = { background: '#1E252A', border: '1px solid #494949', borderRadius: 8, overflow: 'hidden' } as const;
  const cardHeader = { padding: '11px 18px', borderBottom: '1px solid #494949', display: 'flex', alignItems: 'center', justifyContent: 'space-between' } as const;
  const thStyle = { padding: '8px 18px', textAlign: 'left' as const, fontSize: 11, color: '#556070', fontWeight: 500 };
  const tdStyle = { padding: '11px 18px', fontSize: 13, fontWeight: 500 };
  const tdMono = { padding: '11px 18px', fontSize: 11, color: '#8b9aaa', fontFamily: "'JetBrains Mono', monospace" };
  const rowSep = { borderTop: '1px solid #252e35' };

  return (
    <main style={{ padding: '20px 24px', maxWidth: 900, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: 16 }}>

      {/* Page heading */}
      <div style={{ padding: '4px 0 8px' }}>
        <h1 style={{ fontSize: 22, fontWeight: 700, letterSpacing: '-0.4px', color: '#ECEFF1' }}>Configuration</h1>
        <p style={{ fontSize: 12, color: '#9CA3AF', lineHeight: 1.6, marginTop: 4 }}>SaveLocker · Self-hosted cloud save manager</p>
      </div>

      {/* ── Server Settings Card ── */}
      <div style={card}>
        <div style={cardHeader}>
          <span style={{ fontSize: 13, fontWeight: 600, color: '#ECEFF1' }}>Server settings</span>
          <span style={{ fontSize: 11.5, color: '#9CA3AF' }}>SteamGridDB artwork</span>
        </div>

        <div style={{ padding: '16px 18px', display: 'flex', flexDirection: 'column', gap: 14 }}>

          {/* Current key status */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            <span style={{ fontSize: 13, color: '#ECEFF1' }}>SteamGridDB API key:</span>
            {settings.steamGridDbConfigured ? (
              <>
                <span style={{ padding: '2px 7px', background: '#129271', color: '#fff', borderRadius: 4, fontSize: 10, fontWeight: 600, letterSpacing: '0.04em' }}>configured</span>
                <span style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 12, color: '#ECEFF1', letterSpacing: '0.04em' }}>{settings.steamGridDbKeyMasked || ''}</span>
                {settings.steamGridDbFromConfig && (
                  <span style={{ fontSize: 11.5, color: '#9CA3AF' }}>(from config file — saving here overrides it)</span>
                )}
              </>
            ) : (
              <span style={{ padding: '2px 7px', border: '1px solid #f4a60d', color: '#f4a60d', borderRadius: 4, fontSize: 10, fontWeight: 600 }}>not set</span>
            )}
          </div>

          {/* Input + actions */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <input
              type="text"
              value={sgdbInput}
              onChange={e => setSgdbInput(e.target.value)}
              placeholder="Paste SteamGridDB API key"
              style={{ flex: 1, minWidth: 220, padding: '7px 10px', background: 'transparent', color: '#ECEFF1', border: '1px solid #494949', borderRadius: 5, fontSize: 12, fontFamily: "'Inter', sans-serif", transition: 'border-color 0.15s' }}
            />
            <button
              onClick={handleSaveKey}
              style={{ padding: '6px 14px', background: '#129271', color: '#fff', border: 'none', borderRadius: 5, fontSize: 12, fontWeight: 600, cursor: 'pointer', whiteSpace: 'nowrap' }}
            >
              Save key
            </button>
            {settings.steamGridDbConfigured && (
              <button
                onClick={handleClearKey}
                style={{ padding: '6px 12px', background: 'transparent', color: '#ECEFF1', border: '1px solid #494949', borderRadius: 5, fontSize: 12, cursor: 'pointer' }}
              >
                Clear
              </button>
            )}
          </div>
          <p style={{ fontSize: 11, color: '#9CA3AF', marginTop: -6 }}>
            Free key: <a href="https://www.steamgriddb.com" target="_blank" rel="noreferrer" style={{ color: '#129271' }}>steamgriddb.com</a> → user menu → Preferences → API.
          </p>

        </div>
      </div>

      {/* ── Machines / API Keys ── */}
      <div style={{ ...card, marginBottom: 24 }}>
        <div style={cardHeader}>
          <span style={{ fontSize: 13, fontWeight: 600, color: '#ECEFF1' }}>Machines / API keys</span>
          <span style={{ fontSize: 11.5, color: '#9CA3AF' }}>delete unwanted users/keys</span>
        </div>
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr style={{ background: '#222d34', borderBottom: '1px solid #494949' }}>
              <th style={thStyle}>Machine</th>
              <th style={thStyle}>Registered</th>
              <th style={thStyle}>Last seen</th>
              <th style={{ ...thStyle, textAlign: 'right' }}></th>
            </tr>
          </thead>
          <tbody>
            {machines.length === 0
              ? <tr><td colSpan={4} style={{ padding: '20px 18px', color: '#556070', fontSize: 13 }}>No machines registered.</td></tr>
              : machines.map(m => (
                  <tr key={m.id} style={rowSep}>
                    <td style={tdStyle}>{m.name}</td>
                    <td style={tdMono}>{when(m.createdAt)}</td>
                    <td style={tdMono}>{when(m.lastSeen)}</td>
                    <td style={{ padding: '11px 18px', textAlign: 'right' }}>
                      <button
                        onClick={() => handleDeleteMachine(m.id, m.name)}
                        style={{ padding: '4px 10px', border: '1px solid #f4a60d', color: '#f4a60d', background: 'transparent', borderRadius: 4, fontSize: 11, cursor: 'pointer' }}
                      >
                        Delete
                      </button>
                    </td>
                  </tr>
                ))
            }
          </tbody>
        </table>
      </div>

    </main>
  );
}
