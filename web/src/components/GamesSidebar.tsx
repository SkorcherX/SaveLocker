import type { GameSummary } from '../types';

interface Props {
  games: GameSummary[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}

export function GamesSidebar({ games, selectedId, onSelect }: Props) {
  return (
    <aside style={{
      width: 220,
      flexShrink: 0,
      background: '#1E252A',
      borderRight: '1px solid #494949',
      display: 'flex',
      flexDirection: 'column',
      overflowY: 'auto',
      minHeight: 0,
    }}>
      <div style={{ padding: '10px 14px', borderBottom: '1px solid #494949' }}>
        <span style={{ fontSize: 10.5, fontWeight: 600, color: '#8b9aaa', letterSpacing: '0.1em', textTransform: 'uppercase' }}>Games</span>
      </div>

      {games.length === 0 && (
        <div style={{ padding: '20px 14px', fontSize: 12, color: '#556070' }}>No games tracked yet.</div>
      )}

      {games.map(s => {
        const { game, head, hasOpenConflict, lease } = s;
        const isSelected = game.id === selectedId;

        return (
          <button
            key={game.id}
            onClick={() => onSelect(game.id)}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 10,
              padding: '10px 14px',
              background: isSelected ? '#2A3238' : 'transparent',
              border: 'none',
              borderBottom: '1px solid #252e35',
              borderLeft: isSelected ? '2px solid #129271' : '2px solid transparent',
              cursor: 'pointer',
              textAlign: 'left',
              width: '100%',
              opacity: game.enabled ? 1 : 0.55,
            }}
          >
            {/* Box art thumbnail */}
            {game.gridUrl
              ? <img src={game.gridUrl} alt="" style={{ width: 32, height: 46, objectFit: 'cover', borderRadius: 4, border: '1px solid #494949', flexShrink: 0 }} />
              : <div style={{ width: 32, height: 46, background: '#2A3238', border: '1px dashed #494949', borderRadius: 4, flexShrink: 0 }} />
            }

            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: 12, fontWeight: 500, color: '#ECEFF1', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {game.name}
              </div>
              <div style={{ marginTop: 3, display: 'flex', gap: 4, flexWrap: 'wrap' }}>
                {hasOpenConflict
                  ? <span style={{ fontSize: 9, padding: '1px 5px', border: '1px solid #f4a60d', color: '#f4a60d', borderRadius: 3 }}>conflict</span>
                  : <span style={{ fontSize: 9, padding: '1px 5px', border: '1px solid #129271', color: '#129271', borderRadius: 3 }}>in sync</span>
                }
                {lease?.holderMachineName
                  ? <span style={{ fontSize: 9, padding: '1px 5px', border: '1px solid #494949', color: '#8b9aaa', borderRadius: 3 }}>leased</span>
                  : null
                }
                {head && (
                  <span style={{ fontSize: 9, color: '#556070', fontFamily: "'JetBrains Mono', monospace" }}>
                    {head.id.replace(/-/g, '').slice(0, 6)}
                  </span>
                )}
              </div>
            </div>
          </button>
        );
      })}
    </aside>
  );
}
