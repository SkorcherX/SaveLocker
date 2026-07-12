import { useEffect, useState } from 'react';
import { api } from '../api';
import type { GameSummary, Machine, Command, Conflict, Version, MachineSavePath } from '../types';

const shortId = (id: string | null | undefined) => id ? id.replace(/-/g, '').slice(0, 8) : '—';
const asUtc = (t: string) => /[Z+]/.test(t.slice(-6)) ? t : t + 'Z';
const when = (t: string | null | undefined) => t ? new Date(asUtc(t)).toLocaleString() : '—';
const fmtMb = (n: number) => (n / (1024 * 1024)).toFixed(2) + ' MB';

interface Props {
  summary: GameSummary;
  machines: Machine[];
  commands: Command[];
  conflicts: Conflict[];
  onRefresh: () => void;
}

export function GameDetail({ summary, machines, commands, conflicts, onRefresh }: Props) {
  const [versions, setVersions] = useState<Version[]>([]);
  const [loadingVersions, setLoadingVersions] = useState(true);
  const [machinePaths, setMachinePaths] = useState<MachineSavePath[]>([]);
  const [excludeText, setExcludeText] = useState((summary.game.excludeGlobs ?? []).join('\n'));
  const [excludeForGameId, setExcludeForGameId] = useState(summary.game.id);
  const [savingExcludes, setSavingExcludes] = useState(false);
  const [defaultGlobs, setDefaultGlobs] = useState<string[]>([]);
  const [excludeOpen, setExcludeOpen] = useState(false);

  const { game, head, lease, hasOpenConflict } = summary;

  // Reset the exclude editor when switching games (not on every poll — avoids clobbering edits).
  if (excludeForGameId !== game.id) {
    setExcludeForGameId(game.id);
    setExcludeText((game.excludeGlobs ?? []).join('\n'));
  }
  const headId = head?.id ?? null;
  const conflict = conflicts.find(c => c.gameId === game.id) ?? null;
  const gameCmds = commands.filter(c => c.gameId === game.id).slice(0, 8);

  // Latest version per machine (for Machines table "Last upload" column)
  const latestByMachine: Record<string, Version> = {};
  for (const v of versions) {
    if (!latestByMachine[v.machineId]) latestByMachine[v.machineId] = v;
  }

  // Initial-sync wizard: show when multiple machines have versions
  const contributors = Object.values(latestByMachine);

  useEffect(() => {
    setLoadingVersions(true);
    api.versions(game.id).then(vs => { setVersions(vs); setLoadingVersions(false); });
    api.getGamePaths(game.id).then(setMachinePaths).catch(() => {});
  }, [game.id]);

  // Global exclude defaults (read-only display); fetched once.
  useEffect(() => {
    api.settings().then(s => setDefaultGlobs(s.defaultExcludeGlobs ?? [])).catch(() => {});
  }, []);

  async function handleSaveExcludes() {
    const patterns = excludeText.split('\n').map(s => s.trim()).filter(Boolean);
    setSavingExcludes(true);
    try { await api.setExcludes(game.id, patterns); onRefresh(); }
    catch (e) { alert('Could not save exclude patterns: ' + (e as Error).message); }
    finally { setSavingExcludes(false); }
  }

  async function handleRefreshArt() {
    try { await api.refreshArt(game.id); onRefresh(); } catch (e) { alert('Refresh art failed: ' + (e as Error).message); }
  }

  async function handleSetEnabled() {
    try { await api.setEnabled(game.id, !game.enabled); onRefresh(); } catch (e) { alert('Could not change state: ' + (e as Error).message); }
  }

  async function handleDeleteGame() {
    if (!confirm(`Delete "${game.name}"? Removes versions and history from the server. Agents keep local saves.`)) return;
    try { await api.deleteGame(game.id); onRefresh(); } catch (e) { alert('Delete failed: ' + (e as Error).message); }
  }

  async function handleSetSaveDir() {
    const current = game.suggestedSaveDir ?? '';
    const dir = prompt('Suggested save folder fallback (used when a machine has no stored path). Leave blank to clear:', current);
    if (dir === null) return;
    try { await api.setSaveDir(game.id, dir.trim()); onRefresh(); } catch (e) { alert('Could not set save folder: ' + (e as Error).message); }
  }

  async function handleSetMachinePath(machineId: string, machineName: string) {
    const current = machinePaths.find(p => p.machineId === machineId)?.savePath ?? '';
    const path = prompt(`Save folder for ${machineName}:\n(Leave blank to clear the stored path)`, current);
    if (path === null) return;
    try {
      if (path.trim() === '') await api.clearMachinePath(game.id, machineId);
      else await api.setMachinePath(game.id, machineId, path.trim());
      setMachinePaths(await api.getGamePaths(game.id));
    } catch (e) { alert('Could not update path: ' + (e as Error).message); }
  }

  async function handleDeleteVersion(versionId: string) {
    if (!confirm('Delete this version? The archive will be permanently removed from the server.')) return;
    try {
      await api.deleteVersion(game.id, versionId);
      setVersions(await api.versions(game.id));
      onRefresh();
    } catch (e) { alert('Delete failed: ' + (e as Error).message); }
  }

  async function handleSetLatest(versionId: string) {
    if (!confirm('Set this version as Latest? Every machine will pull it on next sync.')) return;
    try {
      await api.setLatest(game.id, versionId);
      const vs = await api.versions(game.id);
      setVersions(vs);
      onRefresh();
    } catch (e) { alert('Set as Latest failed: ' + (e as Error).message); }
  }

  async function handleForceRelease() {
    if (!confirm('Force-release this lease?')) return;
    try { await api.forceRelease(game.id); onRefresh(); } catch (e) { alert('Force-release failed: ' + (e as Error).message); }
  }

  async function handleResolveConflict(versionId: string) {
    if (!conflict) return;
    try { await api.resolveConflict(conflict.id, versionId); onRefresh(); } catch (e) { alert('Resolve failed: ' + (e as Error).message); }
  }

  async function handleCmd(machineId: string, type: string, force: boolean) {
    try { await api.queueCommand(machineId, game.id, type, force); onRefresh(); } catch (e) { alert('Could not queue command: ' + (e as Error).message); }
  }

  const card = { background: '#1E252A', border: '1px solid #494949', borderRadius: 8, overflow: 'hidden' } as const;
  const cardHeader = { padding: '11px 18px', borderBottom: '1px solid #494949' } as const;
  const sectionLabel = { fontSize: 10, fontWeight: 700, color: '#129271', letterSpacing: '0.12em', textTransform: 'uppercase' as const };
  const thStyle = { padding: '8px 18px', textAlign: 'left' as const, fontSize: 11, color: '#556070', fontWeight: 500 };
  const tdStyle = { padding: '11px 18px', fontSize: 13, fontWeight: 500 };
  const tdMono = { padding: '11px 18px', fontSize: 11, color: '#8b9aaa', fontFamily: "'JetBrains Mono', monospace" };
  const rowSep = { borderTop: '1px solid #252e35' };

  const ghostBtn = (extra?: React.CSSProperties): React.CSSProperties => ({
    padding: '2px 8px', border: '1px solid #494949', color: '#ECEFF1', background: 'transparent',
    borderRadius: 4, fontSize: 10, cursor: 'pointer', ...extra,
  });
  const amberBtn: React.CSSProperties = { padding: '2px 8px', border: '1px solid #f4a60d', color: '#f4a60d', background: 'transparent', borderRadius: 4, fontSize: 10, cursor: 'pointer' };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>

      {/* ── Game Details Card ── */}
      <div style={{ ...card, padding: '18px 20px' }}>
        <div style={{ display: 'flex', gap: 18, alignItems: 'flex-start' }}>

          {/* Box art */}
          {game.gridUrl
            ? <img src={game.gridUrl} alt="cover" style={{ width: 94, height: 134, objectFit: 'cover', borderRadius: 6, border: '1px solid #494949', flexShrink: 0 }} />
            : (
              <div style={{ width: 94, height: 134, background: '#2A3238', border: '1px dashed #494949', borderRadius: 6, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 5, flexShrink: 0 }}>
                <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="#494949" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"><rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="8.5" cy="8.5" r="1.5"/><polyline points="21 15 16 10 5 21"/></svg>
                <span style={{ color: '#494949', fontSize: 9, fontFamily: "'JetBrains Mono', monospace", textAlign: 'center', lineHeight: 1.5 }}>box<br/>art</span>
              </div>
            )
          }

          {/* Info column */}
          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 11, minWidth: 0 }}>

            {/* Title + badges + actions */}
            <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 6 }}>
              <span style={{ fontSize: 17, fontWeight: 700, letterSpacing: '-0.3px' }}>{game.name}</span>

              {hasOpenConflict
                ? <span style={{ padding: '2px 7px', border: '1px solid #f4a60d', color: '#f4a60d', borderRadius: 4, fontSize: 10, fontWeight: 600 }}>conflict</span>
                : <span style={{ padding: '2px 7px', border: '1px solid #129271', color: '#129271', borderRadius: 4, fontSize: 10, fontWeight: 600 }}>in sync</span>
              }

              {lease?.holderMachineName
                ? <span style={{ padding: '2px 7px', border: '1px solid #494949', color: '#8b9aaa', borderRadius: 4, fontSize: 10 }}>leased by {lease.holderMachineName}</span>
                : <span style={{ padding: '2px 7px', border: '1px solid #494949', color: '#8b9aaa', borderRadius: 4, fontSize: 10 }}>free</span>
              }

              {!game.enabled && <span style={{ padding: '2px 7px', border: '1px solid #f4a60d', color: '#f4a60d', borderRadius: 4, fontSize: 10 }}>disabled</span>}

              <button style={ghostBtn()} onClick={handleRefreshArt}>Refresh art</button>
              <button style={ghostBtn()} onClick={handleSetEnabled}>{game.enabled ? 'Disable' : 'Enable'}</button>
              {lease?.holderMachineName && (
                <button style={ghostBtn({ borderColor: '#f4a60d', color: '#f4a60d' })} onClick={handleForceRelease}>Force-release lease</button>
              )}
              <button style={amberBtn} onClick={handleDeleteGame}>Delete</button>
            </div>

            {/* Latest commit meta */}
            {head ? (
              <p style={{ fontSize: 11.5, color: '#8b9aaa', fontFamily: "'JetBrains Mono', monospace" }}>
                latest&nbsp;<span style={{ color: '#fdce63', fontWeight: 500 }}>{shortId(head.id)}</span>&nbsp;from&nbsp;
                <span style={{ color: '#ECEFF1' }}>{head.machineName}</span>&nbsp;at&nbsp;
                <span style={{ color: '#ECEFF1' }}>{when(head.createdAt)}</span>&nbsp;·&nbsp;
                <span style={{ color: '#ECEFF1' }}>{fmtMb(head.size)}</span>
              </p>
            ) : (
              <p style={{ fontSize: 11.5, color: '#556070', fontFamily: "'JetBrains Mono', monospace" }}>no saves yet</p>
            )}

            {/* Total storage for this game */}
            <p style={{ fontSize: 11, color: '#556070', fontFamily: "'JetBrains Mono', monospace" }}>
              total stored:&nbsp;
              <span style={{ color: '#8b9aaa', fontWeight: 500 }}>{fmtMb(summary.totalStorageBytes)}</span>
              &nbsp;across&nbsp;
              <span style={{ color: '#8b9aaa' }}>{versions.length} version{versions.length !== 1 ? 's' : ''}</span>
            </p>

            {/* Suggested save dir fallback */}
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, background: '#2A3238', padding: '7px 10px', borderRadius: 5, border: '1px solid #494949' }}>
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="#494949" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>
              <span style={{ fontSize: 10, color: '#556070', flexShrink: 0 }}>fallback path:</span>
              <span style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 11, color: '#8b9aaa', flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {game.suggestedSaveDir || <span style={{ color: '#494949', fontStyle: 'italic' }}>none</span>}
              </span>
              <button style={{ padding: '3px 9px', border: '1px solid #494949', color: '#ECEFF1', background: 'transparent', borderRadius: 4, fontSize: 10, cursor: 'pointer', flexShrink: 0 }} onClick={handleSetSaveDir}>Edit</button>
            </div>

          </div>
        </div>
      </div>

      {/* ── Conflict resolution ── */}
      {conflict && (
        <div style={{ background: '#241a1a', border: '1px solid #4a2a2a', borderRadius: 8, padding: '10px 12px' }}>
          <b style={{ color: '#f4a60d' }}>Conflict — choose the version to keep:</b>
          {' '}
          <a href="#help/conflicts" style={{ fontSize: 11, color: '#129271', textDecoration: 'underline' }}>Why did this happen?</a>
          <div style={{ display: 'flex', gap: 8, marginTop: 8, flexWrap: 'wrap' }}>
            {[conflict.versionAId, conflict.versionBId].map(vid => {
              const v = versions.find(x => x.id === vid);
              return (
                <button key={vid}
                  onClick={() => handleResolveConflict(vid)}
                  style={{ padding: '5px 12px', background: '#129271', color: '#fff', border: 'none', borderRadius: 5, fontSize: 12, cursor: 'pointer' }}
                >
                  Keep {shortId(vid)}{v ? ` from ${v.machineName} (${when(v.createdAt)})` : ''}
                </button>
              );
            })}
          </div>
        </div>
      )}

      {/* ── Initial sync wizard ── */}
      {contributors.length > 1 && (
        <div style={{ background: '#1a2330', border: '1px solid #2a3a52', borderRadius: 8, padding: '10px 12px' }}>
          <b>Initial sync — which machine has your real progress?</b>
          <p style={{ fontSize: 12, color: '#8b9aaa', marginTop: 2 }}>Sets that machine's newest save as Latest (what every machine pulls).</p>
          <div style={{ display: 'flex', gap: 8, marginTop: 8, flexWrap: 'wrap' }}>
            {contributors.map(v => (
              <button key={v.id} onClick={() => handleSetLatest(v.id)}
                style={{ padding: '5px 12px', background: '#129271', color: '#fff', border: 'none', borderRadius: 5, fontSize: 12, cursor: 'pointer' }}
              >
                {v.machineName} ({when(v.createdAt)}){v.id === headId ? ' — current' : ''}
              </button>
            ))}
          </div>
        </div>
      )}

      {/* ── Exclude Patterns (collapsible) ── */}
      <div style={card}>
        <button
          onClick={() => setExcludeOpen(o => !o)}
          style={{ width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '11px 18px', background: 'transparent', border: 'none', cursor: 'pointer', textAlign: 'left' }}
        >
          <span style={sectionLabel}>Exclude patterns</span>
          <span style={{ fontSize: 11, color: '#556070', userSelect: 'none' }}>{excludeOpen ? '▲' : '▼'}</span>
        </button>
        {excludeOpen && (
          <div style={{ padding: '0 18px 14px', display: 'flex', flexDirection: 'column', gap: 8, borderTop: '1px solid #494949' }}>
            <p style={{ fontSize: 11, color: '#556070', marginTop: 10 }}>
              One glob per line — e.g. <code style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 10 }}>*.log</code>, <code style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 10 }}>cache/**</code>. Bare patterns match at any depth. See <a href="#help/glob-patterns" style={{ color: '#129271' }}>glob pattern docs</a>.
            </p>
            <textarea
              value={excludeText}
              onChange={e => setExcludeText(e.target.value)}
              spellCheck={false}
              rows={4}
              placeholder="(none — only global defaults apply)"
              style={{ width: '100%', resize: 'vertical', background: '#2A3238', color: '#ECEFF1', border: '1px solid #494949', borderRadius: 4, fontFamily: "'JetBrains Mono', monospace", fontSize: 11, padding: '6px 8px', boxSizing: 'border-box' }}
            />
            {defaultGlobs.length > 0 && (
              <span style={{ fontSize: 10, color: '#556070' }}>
                global defaults (always applied):&nbsp;
                <span style={{ fontFamily: "'JetBrains Mono', monospace", color: '#8b9aaa' }}>{defaultGlobs.join(', ')}</span>
              </span>
            )}
            <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
              <button
                disabled={savingExcludes}
                onClick={handleSaveExcludes}
                style={{ padding: '5px 14px', border: `1px solid ${savingExcludes ? '#494949' : '#129271'}`, color: savingExcludes ? '#556070' : '#129271', background: 'transparent', borderRadius: 4, fontSize: 11, cursor: savingExcludes ? 'default' : 'pointer' }}
              >
                {savingExcludes ? 'Saving…' : 'Save patterns'}
              </button>
            </div>
          </div>
        )}
      </div>

      {/* ── Machines Table ── */}
      <div style={card}>
        <div style={cardHeader}><span style={sectionLabel}>Machines</span></div>
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr style={{ background: '#222d34' }}>
              <th style={thStyle}>Machine</th>
              <th style={thStyle}>Last upload (this game)</th>
              <th style={thStyle}>Last seen</th>
              <th style={thStyle}>Remote actions</th>
            </tr>
          </thead>
          <tbody>
            {machines.length === 0
              ? <tr><td colSpan={4} style={{ padding: '20px 18px', color: '#556070', fontSize: 13 }}>No machines registered.</td></tr>
              : machines.map(m => {
                  const last = latestByMachine[m.id];
                  return (
                    <tr key={m.id} style={rowSep}>
                      <td style={tdStyle}>{m.name}</td>
                      <td style={tdMono}>{last ? when(last.createdAt) : '—'}</td>
                      <td style={tdMono}>{when(m.lastSeen)}</td>
                      <td style={{ padding: '11px 18px' }}>
                        <div style={{ display: 'flex', gap: 5 }}>
                          <button style={{ padding: '4px 10px', border: '1px solid #494949', color: '#ECEFF1', background: 'transparent', borderRadius: 4, fontSize: 11, cursor: 'pointer' }} onClick={() => handleCmd(m.id, 'Pull', true)}>Pull</button>
                          <button style={{ padding: '4px 10px', border: '1px solid #494949', color: '#ECEFF1', background: 'transparent', borderRadius: 4, fontSize: 11, cursor: 'pointer' }} onClick={() => handleCmd(m.id, 'Push', true)}>Push</button>
                          <button style={{ padding: '4px 10px', border: 'none', color: '#fff', background: '#129271', borderRadius: 4, fontSize: 11, cursor: 'pointer', fontWeight: 500 }} onClick={() => handleCmd(m.id, 'Sync', false)}>Sync</button>
                        </div>
                      </td>
                    </tr>
                  );
                })
            }
          </tbody>
        </table>
      </div>

      {/* ── Save paths per machine ── */}
      <div style={card}>
        <div style={cardHeader}><span style={sectionLabel}>Save paths per machine</span></div>
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr style={{ background: '#222d34' }}>
              <th style={thStyle}>Machine</th>
              <th style={thStyle}>Save folder</th>
              <th style={{ ...thStyle, width: 80 }}></th>
            </tr>
          </thead>
          <tbody>
            {machines.length === 0
              ? <tr><td colSpan={3} style={{ padding: '20px 18px', color: '#556070', fontSize: 13 }}>No machines registered.</td></tr>
              : machines.map(m => {
                  const stored = machinePaths.find(p => p.machineId === m.id);
                  return (
                    <tr key={m.id} style={rowSep}>
                      <td style={tdStyle}>{m.name}</td>
                      <td style={tdMono}>
                        {stored
                          ? stored.savePath
                          : <span style={{ color: '#556070', fontStyle: 'italic' }}>not set</span>}
                      </td>
                      <td style={{ padding: '11px 18px' }}>
                        <button style={ghostBtn()} onClick={() => handleSetMachinePath(m.id, m.name)}>
                          {stored ? 'Edit' : 'Set'}
                        </button>
                      </td>
                    </tr>
                  );
                })
            }
          </tbody>
        </table>
      </div>

      {/* ── Recent Remote Commands ── */}
      {gameCmds.length > 0 && (
        <div style={card}>
          <div style={cardHeader}><span style={sectionLabel}>Recent Remote Commands</span></div>
          <table style={{ width: '100%', borderCollapse: 'collapse' }}>
            <thead>
              <tr style={{ background: '#222d34' }}>
                <th style={{ ...thStyle, whiteSpace: 'nowrap' }}>When</th>
                <th style={thStyle}>Machine</th>
                <th style={thStyle}>Action</th>
                <th style={thStyle}>Status</th>
                <th style={thStyle}>Result</th>
              </tr>
            </thead>
            <tbody>
              {gameCmds.map(c => (
                <tr key={c.id} style={rowSep}>
                  <td style={{ ...tdMono, whiteSpace: 'nowrap' }}>{when(c.createdAt)}</td>
                  <td style={tdStyle}>{c.machineName}</td>
                  <td style={{ padding: '11px 18px', fontSize: 12, color: '#ECEFF1' }}>{c.type}{c.force ? ' (force)' : ''}</td>
                  <td style={{ padding: '11px 18px', fontSize: 12, fontWeight: 600, whiteSpace: 'nowrap' }}>
                    {c.status === 'Done'
                      ? <span style={{ color: '#129271' }}>Done</span>
                      : c.status === 'Failed'
                        ? <span style={{ color: '#f4a60d' }}>Failed</span>
                        : <span style={{ color: '#8b9aaa' }}>{c.status}</span>
                    }
                  </td>
                  <td style={{ padding: '11px 18px', fontSize: 11.5, color: '#8b9aaa', maxWidth: 340, wordBreak: 'break-word', lineHeight: 1.6 }}>
                    {c.result || (c.status === 'Pending' ? 'awaiting next poll…' : '—')}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* ── Versions ── */}
      <div style={{ ...card, marginBottom: 24 }}>
        <div style={cardHeader}><span style={sectionLabel}>Versions</span></div>
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr style={{ background: '#222d34' }}>
              <th style={thStyle}>Version</th>
              <th style={thStyle}>Machine</th>
              <th style={thStyle}>When</th>
              <th style={thStyle}>Size</th>
              <th style={thStyle}></th>
            </tr>
          </thead>
          <tbody>
            {loadingVersions
              ? <tr><td colSpan={5} style={{ padding: '20px 18px', color: '#556070', fontSize: 13 }}>Loading…</td></tr>
              : versions.length === 0
                ? <tr><td colSpan={5} style={{ padding: '20px 18px', color: '#556070', fontSize: 13 }}>No versions yet.</td></tr>
                : versions.map(v => (
                    <tr key={v.id} style={rowSep}>
                      <td style={{ padding: '11px 18px' }}>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
                          <span style={{ fontFamily: "'JetBrains Mono', monospace", fontSize: 12, color: '#fdce63' }}>{shortId(v.id)}</span>
                          {v.id === headId
                            ? <span style={{ padding: '2px 7px', background: '#129271', color: '#fff', borderRadius: 3, fontSize: 10, fontWeight: 600, letterSpacing: '0.04em' }}>Latest</span>
                            : <button style={{ padding: '2px 8px', border: '1px solid #f4a60d', color: '#f4a60d', background: 'transparent', borderRadius: 3, fontSize: 10, cursor: 'pointer' }} onClick={() => handleSetLatest(v.id)}>Set as Latest</button>
                          }
                        </div>
                      </td>
                      <td style={tdStyle}>{v.machineName}</td>
                      <td style={tdMono}>{when(v.createdAt)}</td>
                      <td style={{ padding: '11px 18px', fontSize: 11.5, color: '#8b9aaa' }}>{fmtMb(v.size)}</td>
                      <td style={{ padding: '11px 18px' }}>
                        {v.id !== headId && (
                          <button
                            onClick={() => handleDeleteVersion(v.id)}
                            style={{ padding: '2px 8px', border: '1px solid #f4a60d', color: '#f4a60d', background: 'transparent', borderRadius: 3, fontSize: 10, cursor: 'pointer' }}
                          >
                            Delete
                          </button>
                        )}
                      </td>
                    </tr>
                  ))
            }
          </tbody>
        </table>
      </div>

    </div>
  );
}
