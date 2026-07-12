import { useState, useEffect, useRef } from 'react';
import { api, setPassword } from '../api';
import type { GameSummary, Machine, Settings, AgentInstallerStatus } from '../types';

interface Props {
  games: GameSummary[];
  machines: Machine[];
  settings: Settings;
  onRefresh: () => void;
}

const asUtc = (t: string) => /[Z+]/.test(t.slice(-6)) ? t : t + 'Z';
const when = (t: string | null | undefined) => t ? new Date(asUtc(t)).toLocaleString() : '—';

export function ConfigView({ games, machines, settings, onRefresh }: Props) {
  const [sgdbInput, setSgdbInput] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');

  // ── Agent installer state ──
  const [installer, setInstaller] = useState<AgentInstallerStatus | null>(null);
  const [installerLoading, setInstallerLoading] = useState(true);
  const [uploading, setUploading] = useState(false);
  const [versionOverride, setVersionOverride] = useState('');
  const fileInputRef = useRef<HTMLInputElement>(null);

  async function loadInstaller() {
    setInstallerLoading(true);
    try { setInstaller(await api.installerStatus()); }
    catch { /* non-fatal */ }
    finally { setInstallerLoading(false); }
  }

  useEffect(() => { loadInstaller(); }, []);

  function parseVersionFromName(name: string): string {
    const m = name.match(/Setup-(.+?)\.exe$/i);
    return m ? m[1] : '';
  }

  async function handleUpload() {
    const file = fileInputRef.current?.files?.[0];
    if (!file) { alert('Choose an installer file first.'); return; }
    const ver = versionOverride.trim() || parseVersionFromName(file.name);
    if (!ver) { alert('Could not parse version from filename. Enter it in the Version field.'); return; }
    setUploading(true);
    try {
      const fd = new FormData();
      fd.append('file', file);
      await api.uploadInstaller(fd, ver);
      setVersionOverride('');
      if (fileInputRef.current) fileInputRef.current.value = '';
      await loadInstaller();
    } catch (e) { alert('Upload failed: ' + (e as Error).message); }
    finally { setUploading(false); }
  }

  async function handleDeleteInstaller() {
    if (!confirm('Remove the hosted installer? Agents will no longer be offered an update until a new installer is uploaded.')) return;
    try { await api.deleteInstaller(); await loadInstaller(); }
    catch (e) { alert('Delete failed: ' + (e as Error).message); }
  }

  const [fetchingGitHub, setFetchingGitHub] = useState(false);
  async function handleFetchGitHub() {
    setFetchingGitHub(true);
    try {
      const info = await api.fetchInstallerFromGitHub();
      await loadInstaller();
      alert(`Fetched v${info.version} (${info.fileName}) from GitHub.`);
    } catch (e) { alert('Fetch from GitHub failed: ' + (e as Error).message); }
    finally { setFetchingGitHub(false); }
  }
  // Per-game retention inputs: gameId -> string (empty = use default)
  const [retentionInputs, setRetentionInputs] = useState<Record<string, string>>(
    () => Object.fromEntries(games.map(s => [s.game.id, s.game.retainVersions?.toString() ?? '']))
  );

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

  async function handleSetPassword() {
    if (!newPassword) { alert('Enter a new password.'); return; }
    if (newPassword !== confirmPassword) { alert('Passwords do not match.'); return; }
    try {
      const res = await api.setAdminPassword(newPassword);
      setPassword(newPassword);
      setNewPassword('');
      setConfirmPassword('');
      alert(res.message);
      onRefresh();
    } catch (e) { alert('Could not set password: ' + (e as Error).message); }
  }

  async function handleClearPassword() {
    if (!confirm('Remove the admin password? The dashboard will be accessible to anyone on your network.')) return;
    try {
      await api.setAdminPassword(null);
      setPassword('');
      onRefresh();
    } catch (e) { alert('Could not clear password: ' + (e as Error).message); }
  }

  async function handleSaveRetention(gameId: string, gameName: string) {
    const raw = retentionInputs[gameId]?.trim();
    const value = raw === '' ? null : parseInt(raw, 10);
    if (value !== null && (isNaN(value) || value < 0)) { alert('Enter a positive number, or leave blank to use the server default.'); return; }
    try {
      await api.setRetention(gameId, value);
      onRefresh();
    } catch (e) { alert(`Could not update retention for ${gameName}: ` + (e as Error).message); }
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

      {/* ── Admin Password ── */}
      <div style={card}>
        <div style={cardHeader}>
          <span style={{ fontSize: 13, fontWeight: 600, color: '#ECEFF1' }}>Admin password</span>
          <span style={{ fontSize: 11.5, color: '#9CA3AF' }}>dashboard access control</span>
        </div>
        <div style={{ padding: '16px 18px', display: 'flex', flexDirection: 'column', gap: 14 }}>

          <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            <span style={{ fontSize: 13, color: '#ECEFF1' }}>Status:</span>
            {settings.adminPasswordSet ? (
              <span style={{ padding: '2px 7px', background: '#129271', color: '#fff', borderRadius: 4, fontSize: 10, fontWeight: 600, letterSpacing: '0.04em' }}>protected</span>
            ) : (
              <span style={{ padding: '2px 7px', border: '1px solid #f4a60d', color: '#f4a60d', borderRadius: 4, fontSize: 10, fontWeight: 600 }}>open — no password set</span>
            )}
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <input
              type="password"
              value={newPassword}
              onChange={e => setNewPassword(e.target.value)}
              placeholder={settings.adminPasswordSet ? 'New password' : 'Set password'}
              style={{ flex: 1, minWidth: 160, padding: '7px 10px', background: 'transparent', color: '#ECEFF1', border: '1px solid #494949', borderRadius: 5, fontSize: 12, fontFamily: "'Inter', sans-serif" }}
            />
            <input
              type="password"
              value={confirmPassword}
              onChange={e => setConfirmPassword(e.target.value)}
              placeholder="Confirm password"
              onKeyDown={e => e.key === 'Enter' && handleSetPassword()}
              style={{ flex: 1, minWidth: 160, padding: '7px 10px', background: 'transparent', color: '#ECEFF1', border: '1px solid #494949', borderRadius: 5, fontSize: 12, fontFamily: "'Inter', sans-serif" }}
            />
            <button
              onClick={handleSetPassword}
              style={{ padding: '6px 14px', background: '#129271', color: '#fff', border: 'none', borderRadius: 5, fontSize: 12, fontWeight: 600, cursor: 'pointer', whiteSpace: 'nowrap' }}
            >
              {settings.adminPasswordSet ? 'Change password' : 'Set password'}
            </button>
            {settings.adminPasswordSet && (
              <button
                onClick={handleClearPassword}
                style={{ padding: '6px 12px', background: 'transparent', color: '#ECEFF1', border: '1px solid #494949', borderRadius: 5, fontSize: 12, cursor: 'pointer' }}
              >
                Remove
              </button>
            )}
          </div>
          <p style={{ fontSize: 11, color: '#9CA3AF', marginTop: -6 }}>
            Protects the dashboard from casual access on your local network. Enter your password in the nav bar to connect.
          </p>

        </div>
      </div>

      {/* ── Agent Updates ── */}
      <div style={card}>
        <div style={cardHeader}>
          <span style={{ fontSize: 13, fontWeight: 600, color: '#ECEFF1' }}>Agent updates</span>
          <span style={{ fontSize: 11.5, color: '#9CA3AF' }}>installer management</span>
        </div>
        <div style={{ padding: '16px 18px', display: 'flex', flexDirection: 'column', gap: 14 }}>

          {/* Current installer status */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            <span style={{ fontSize: 13, color: '#ECEFF1' }}>Hosted installer:</span>
            {installerLoading ? (
              <span style={{ fontSize: 12, color: '#556070' }}>Loading…</span>
            ) : installer ? (
              <>
                <span style={{ padding: '2px 7px', background: '#129271', color: '#fff', borderRadius: 4, fontSize: 10, fontWeight: 600, letterSpacing: '0.04em' }}>v{installer.version}</span>
                <span style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 11, color: '#9CA3AF' }}>{installer.fileName}</span>
                <span style={{ fontSize: 11, color: '#556070' }}>·</span>
                <span style={{ fontSize: 11, color: '#556070' }}>{(installer.sizeBytes / (1024 * 1024)).toFixed(1)} MB</span>
                <span style={{ fontSize: 11, color: '#556070' }}>· uploaded {new Date(asUtc(installer.uploadedAt)).toLocaleDateString()}</span>
                <a
                  href="/api/agent/installer/download"
                  style={{ fontSize: 11, color: '#129271', textDecoration: 'none' }}
                  target="_blank" rel="noreferrer"
                >
                  Download ↓
                </a>
                <button
                  onClick={handleDeleteInstaller}
                  style={{ padding: '2px 10px', border: '1px solid #f4a60d', color: '#f4a60d', background: 'transparent', borderRadius: 4, fontSize: 11, cursor: 'pointer' }}
                >
                  Delete
                </button>
              </>
            ) : (
              <span style={{ padding: '2px 7px', border: '1px solid #556070', color: '#556070', borderRadius: 4, fontSize: 10, fontWeight: 600 }}>none — agents won't be offered updates</span>
            )}
          </div>

          {/* Upload form */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <input
              ref={fileInputRef}
              type="file"
              accept=".exe"
              onChange={e => {
                const name = e.target.files?.[0]?.name ?? '';
                const parsed = parseVersionFromName(name);
                if (parsed) setVersionOverride(parsed);
              }}
              style={{ flex: 1, minWidth: 200, padding: '5px 0', color: '#9CA3AF', fontSize: 12, background: 'transparent', border: 'none' }}
            />
            <input
              type="text"
              value={versionOverride}
              onChange={e => setVersionOverride(e.target.value)}
              placeholder="Version (e.g. 0.2.0)"
              style={{ width: 140, padding: '7px 10px', background: 'transparent', color: '#ECEFF1', border: '1px solid #494949', borderRadius: 5, fontSize: 12, fontFamily: "'JetBrains Mono', monospace" }}
            />
            <button
              onClick={handleUpload}
              disabled={uploading}
              style={{ padding: '6px 14px', background: uploading ? '#2A3238' : '#129271', color: uploading ? '#556070' : '#fff', border: 'none', borderRadius: 5, fontSize: 12, fontWeight: 600, cursor: uploading ? 'default' : 'pointer', whiteSpace: 'nowrap' }}
            >
              {uploading ? 'Uploading…' : 'Upload installer'}
            </button>
          </div>
          <p style={{ fontSize: 11, color: '#9CA3AF', marginTop: -6 }}>
            Upload a <code style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 10 }}>SaveLocker-Agent-Setup-x.y.z.exe</code> built from the release workflow. Version is auto-parsed from the filename. Once uploaded, connected agents will be offered the update at their next check-in.
          </p>

          {/* Fetch from GitHub */}
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap', borderTop: '1px solid #2A3238', paddingTop: 14 }}>
            <button
              onClick={handleFetchGitHub}
              disabled={fetchingGitHub}
              style={{ padding: '6px 14px', background: 'transparent', color: fetchingGitHub ? '#556070' : '#129271', border: `1px solid ${fetchingGitHub ? '#2A3238' : '#129271'}`, borderRadius: 5, fontSize: 12, fontWeight: 600, cursor: fetchingGitHub ? 'default' : 'pointer', whiteSpace: 'nowrap' }}
            >
              {fetchingGitHub ? 'Fetching…' : 'Fetch latest from GitHub'}
            </button>
            <span style={{ fontSize: 11, color: '#9CA3AF' }}>
              Pulls the newest release installer from GitHub and hosts it — no manual download needed.
            </span>
          </div>

        </div>
      </div>

      {/* ── Save retention ── */}
      <div style={card}>
        <div style={cardHeader}>
          <span style={{ fontSize: 13, fontWeight: 600, color: '#ECEFF1' }}>Save retention</span>
          <span style={{ fontSize: 11.5, color: '#9CA3AF' }}>versions stored per game</span>
        </div>
        <div style={{ padding: '10px 18px 4px', fontSize: 11, color: '#556070' }}>
          Leave blank to use the server default (10). Set to 0 for unlimited. Changes take effect on the next upload.
        </div>
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr style={{ background: '#222d34', borderBottom: '1px solid #494949' }}>
              <th style={thStyle}>Game</th>
              <th style={thStyle}>Storage used</th>
              <th style={{ ...thStyle, width: 160 }}>Keep versions</th>
              <th style={{ ...thStyle, width: 80 }}></th>
            </tr>
          </thead>
          <tbody>
            {games.length === 0
              ? <tr><td colSpan={4} style={{ padding: '20px 18px', color: '#556070', fontSize: 13 }}>No games tracked yet.</td></tr>
              : games
                  .slice()
                  .sort((a, b) => b.totalStorageBytes - a.totalStorageBytes)
                  .map(s => (
                    <tr key={s.game.id} style={rowSep}>
                      <td style={tdStyle}>{s.game.name}</td>
                      <td style={tdMono}>{(s.totalStorageBytes / (1024 * 1024)).toFixed(2)} MB</td>
                      <td style={{ padding: '8px 18px' }}>
                        <input
                          type="number"
                          min={0}
                          value={retentionInputs[s.game.id] ?? ''}
                          onChange={e => setRetentionInputs(prev => ({ ...prev, [s.game.id]: e.target.value }))}
                          placeholder="default (10)"
                          style={{ width: '100%', padding: '5px 8px', background: 'transparent', color: '#ECEFF1', border: '1px solid #494949', borderRadius: 4, fontSize: 12, fontFamily: "'JetBrains Mono', monospace" }}
                        />
                      </td>
                      <td style={{ padding: '8px 18px' }}>
                        <button
                          onClick={() => handleSaveRetention(s.game.id, s.game.name)}
                          style={{ padding: '4px 12px', background: '#129271', color: '#fff', border: 'none', borderRadius: 4, fontSize: 11, fontWeight: 600, cursor: 'pointer' }}
                        >
                          Save
                        </button>
                      </td>
                    </tr>
                  ))
            }
          </tbody>
        </table>
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
