import { useState } from 'react';
import { getPassword, setPassword as persistPassword } from '../api';
import type { AgentEvent, ServerBuildInfo } from '../types';
import logoUrl from '../assets/SaveLocker_Logo_crop.png';

type View = 'games' | 'config' | 'audit' | 'help' | 'whats-new';

interface Props {
  view: View;
  onViewChange: (v: View) => void;
  onConnect: () => void;
  onRefresh: () => void;
  /** What this console is running. Undefined until /api/admin/status answers. */
  build?: ServerBuildInfo;
  /** True when the running release's notes have not been opened yet. */
  unreadNotes?: boolean;
  /** Open problems reported by agents, worst first. This is the only way a headless Deck's
   *  failures reach a human — it cannot toast, so the console has to (Decisions.md §2). */
  problems?: AgentEvent[];
  onDismissProblem?: (id: string) => void;
}

const asUtcTime = (t: string) => /[Z+]/.test(t.slice(-6)) ? t : t + 'Z';

function ago(t: string): string {
  const mins = Math.max(0, Math.round((Date.now() - new Date(asUtcTime(t)).getTime()) / 60000));
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}

export function NavBar({ view, onViewChange, onConnect, onRefresh, build, unreadNotes = false, problems = [], onDismissProblem }: Props) {
  const [keyInput, setKeyInput] = useState(getPassword());
  const [showProblems, setShowProblems] = useState(false);

  function handleConnect() {
    persistPassword(keyInput.trim());
    onConnect();
  }

  const errorCount = problems.filter(p => p.severity === 'Error').length;
  const badgeColor = errorCount > 0 ? '#e5534b' : '#f4a60d';

  const navBtn = (active: boolean) =>
    `px-[14px] py-[5px] rounded-[5px] text-[12px] border cursor-pointer font-[inherit] ` +
    (active
      ? 'bg-accent-green text-white border-accent-green font-semibold'
      : 'bg-transparent text-text-primary border-border');

  const ghostBtn = `px-[13px] py-[5px] rounded-[5px] text-[12px] bg-transparent text-text-primary border border-border cursor-pointer font-[inherit]`;

  return (
    <header
      style={{
        background: '#1E252A',
        borderBottom: '1px solid #494949',
        padding: '0 20px',
        height: 72,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        position: 'sticky',
        top: 0,
        zIndex: 20,
      }}
    >
      {/* Brand + version */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        <a
          href="#"
          onClick={e => { e.preventDefault(); onViewChange('games'); }}
          style={{ display: 'flex', alignItems: 'center', gap: 9, userSelect: 'none' }}
        >
          <img src={logoUrl} style={{ height: 64, width: 'auto', borderRadius: 6, flexShrink: 0 }} alt="SaveLocker" />
          <span style={{ fontSize: 17, fontWeight: 700, letterSpacing: '-0.4px' }}>
            Save<span style={{ color: '#129271' }}>Locker</span>
          </span>
        </a>

        {/* What the console is running, always on screen. Answering "is my fix deployed?" should
            not require opening a page, let alone reading a Docker tag on another machine. */}
        <button
          onClick={() => onViewChange('whats-new')}
          title={
            build
              ? `SaveLocker console ${build.version}` +
                (build.commit ? ` (commit ${build.commit})` : '') +
                (build.builtAt ? ` — built ${new Date(build.builtAt).toLocaleString()}` : '') +
                `\nClick for release notes.`
              : 'Release notes'
          }
          style={{
            display: 'flex', alignItems: 'center', gap: 6,
            padding: '2px 9px', borderRadius: 20, cursor: 'pointer',
            background: view === 'whats-new' ? '#2A3238' : 'transparent',
            border: '1px solid #494949',
            color: build?.isRelease === false ? '#f4a60d' : '#8b9aaa',
            fontSize: 11, fontFamily: "'JetBrains Mono', monospace",
          }}
        >
          {build ? (build.version === 'dev' ? 'dev' : `v${build.version}`) : '—'}
          {unreadNotes && (
            <span
              title="New release notes"
              style={{ width: 6, height: 6, borderRadius: '50%', background: '#129271', flexShrink: 0 }}
            />
          )}
        </button>
      </div>

      {/* Controls */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        <button className={navBtn(view === 'games')} onClick={() => onViewChange('games')}>Games</button>
        <button className={navBtn(view === 'config')} onClick={() => onViewChange('config')}>Configuration</button>
        <button className={navBtn(view === 'audit')} onClick={() => onViewChange('audit')}>Audit Log</button>
        <button className={navBtn(view === 'help')} onClick={() => onViewChange('help')}>Help</button>
        <button className={navBtn(view === 'whats-new')} onClick={() => onViewChange('whats-new')}>What's New</button>

        {/* API Key composite input */}
        <div style={{ display: 'flex', alignItems: 'center', background: '#2A3238', border: '1px solid #494949', borderRadius: 5, overflow: 'hidden' }}>
          <span style={{ padding: '5px 9px', fontSize: 10, color: '#64748b', fontFamily: "'JetBrains Mono', monospace", borderRight: '1px solid #494949', userSelect: 'none' }}>
            PASSWORD
          </span>
          <input
            type="password"
            value={keyInput}
            onChange={e => setKeyInput(e.target.value)}
            onKeyDown={e => e.key === 'Enter' && handleConnect()}
            style={{ padding: '5px 9px', background: 'transparent', color: '#ECEFF1', border: 'none', fontSize: 11, fontFamily: "'JetBrains Mono', monospace", width: 160 }}
          />
        </div>

        <button
          onClick={handleConnect}
          style={{ padding: '5px 14px', background: '#129271', color: '#fff', border: '1px solid #129271', borderRadius: 5, fontSize: 12, fontWeight: 600 }}
        >
          Connect
        </button>

        <button className={ghostBtn} onClick={onRefresh}>↻ Refresh</button>

        {/* Agent problems. Absent when there are none — a healthy fleet should be quiet. */}
        {problems.length > 0 && (
          <div style={{ position: 'relative' }}>
            <button
              onClick={() => setShowProblems(v => !v)}
              title="Problems reported by agents"
              style={{
                padding: '5px 12px', background: 'transparent', color: badgeColor,
                border: `1px solid ${badgeColor}`, borderRadius: 5, fontSize: 12,
                fontWeight: 600, cursor: 'pointer', display: 'flex', alignItems: 'center', gap: 6,
              }}
            >
              ⚠ {problems.length}
            </button>

            {showProblems && (
              <div
                style={{
                  position: 'absolute', right: 0, top: 'calc(100% + 8px)', width: 460,
                  background: '#1E252A', border: '1px solid #494949', borderRadius: 8,
                  boxShadow: '0 10px 30px rgba(0,0,0,0.45)', zIndex: 30, overflow: 'hidden',
                }}
              >
                <div style={{ padding: '10px 14px', borderBottom: '1px solid #494949', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <span style={{ fontSize: 12.5, fontWeight: 600, color: '#ECEFF1' }}>Agent problems</span>
                  <span style={{ fontSize: 11, color: '#9CA3AF' }}>reported by the machines themselves</span>
                </div>

                <div style={{ maxHeight: 360, overflowY: 'auto' }}>
                  {problems.map(p => (
                    <div key={p.id} style={{ padding: '11px 14px', borderTop: '1px solid #252e35', display: 'flex', gap: 10, alignItems: 'flex-start' }}>
                      <span
                        style={{
                          marginTop: 2, flexShrink: 0, padding: '1px 7px', borderRadius: 3, fontSize: 10,
                          fontWeight: 700, letterSpacing: '0.3px',
                          color: p.severity === 'Error' ? '#e5534b' : '#f4a60d',
                          border: `1px solid ${p.severity === 'Error' ? '#e5534b' : '#f4a60d'}`,
                        }}
                      >
                        {p.severity === 'Error' ? 'ERROR' : 'WARN'}
                      </span>

                      <div style={{ flex: 1, minWidth: 0 }}>
                        <div style={{ fontSize: 12.5, fontWeight: 600, color: '#ECEFF1' }}>
                          {p.machineName}{p.gameName ? ` — ${p.gameName}` : ''}
                        </div>
                        <div style={{ fontSize: 12, color: '#8b9aaa', lineHeight: 1.45, marginTop: 2 }}>{p.message}</div>
                        <div style={{ fontSize: 10.5, color: '#556070', marginTop: 3, fontFamily: "'JetBrains Mono', monospace" }}>
                          {p.code} · {ago(p.lastSeen)}{p.count > 1 ? ` · ×${p.count}` : ''}
                        </div>
                      </div>

                      {onDismissProblem && (
                        <button
                          onClick={() => onDismissProblem(p.id)}
                          title="Dismiss. If the condition still holds, the agent will report it again."
                          style={{ flexShrink: 0, padding: '3px 9px', background: 'transparent', color: '#8b9aaa', border: '1px solid #494949', borderRadius: 4, fontSize: 11, cursor: 'pointer' }}
                        >
                          Dismiss
                        </button>
                      )}
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}
      </div>
    </header>
  );
}
