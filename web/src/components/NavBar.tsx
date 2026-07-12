import { useState } from 'react';
import { getPassword, setPassword as persistPassword } from '../api';
import logoUrl from '../assets/SaveLocker_Logo_crop.png';

type View = 'games' | 'config' | 'audit' | 'help';

interface Props {
  view: View;
  onViewChange: (v: View) => void;
  onConnect: () => void;
  onAddGame: () => void;
  onRefresh: () => void;
  isGamesView: boolean;
}

export function NavBar({ view, onViewChange, onConnect, onAddGame, onRefresh, isGamesView }: Props) {
  const [keyInput, setKeyInput] = useState(getPassword());

  function handleConnect() {
    persistPassword(keyInput.trim());
    onConnect();
  }

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
      {/* Brand */}
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

      {/* Controls */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        <button className={navBtn(view === 'games')} onClick={() => onViewChange('games')}>Games</button>
        <button className={navBtn(view === 'config')} onClick={() => onViewChange('config')}>Configuration</button>
        <button className={navBtn(view === 'audit')} onClick={() => onViewChange('audit')}>Audit Log</button>
        <button className={navBtn(view === 'help')} onClick={() => onViewChange('help')}>Help</button>

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

        {isGamesView && (
          <button className={ghostBtn} onClick={onAddGame}>+ Add game</button>
        )}

        <button className={ghostBtn} onClick={onRefresh}>↻ Refresh</button>
      </div>
    </header>
  );
}
