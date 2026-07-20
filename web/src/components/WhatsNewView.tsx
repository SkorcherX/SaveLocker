import { useState, useEffect } from 'react';
import ReactMarkdown from 'react-markdown';
import { releases, releaseFor } from '../releases/index';
import type { ServerBuildInfo } from '../types';

function getVersionFromHash(): string | null {
  const m = location.hash.match(/^#whats-new\/(.+)$/);
  return m ? m[1] : null;
}

export function WhatsNewView({ build }: { build?: ServerBuildInfo }) {
  // Open on the release this build descends from, so a dev build lands on the last real
  // release rather than on nothing.
  const running = releaseFor(build?.version);
  const [selected, setSelected] = useState<string>(
    getVersionFromHash() ?? running?.version ?? releases[0].version
  );

  useEffect(() => {
    function onHash() {
      const v = getVersionFromHash();
      if (v) setSelected(v);
    }
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);

  function select(version: string) {
    setSelected(version);
    location.hash = `whats-new/${version}`;
  }

  const current = releases.find(r => r.version === selected) ?? releases[0];

  return (
    <div style={{ display: 'flex', flex: 1, minHeight: 0 }}>
      <aside style={{
        width: 240, flexShrink: 0, background: '#1E252A',
        borderRight: '1px solid #494949', display: 'flex', flexDirection: 'column', overflowY: 'auto',
      }}>
        <div style={{ padding: '8px 14px 4px', fontSize: 10, fontWeight: 700, color: '#129271', textTransform: 'uppercase', letterSpacing: '0.12em', borderBottom: '1px solid #252e35' }}>
          Releases
        </div>

        {releases.map(r => {
          const active = r.version === selected;
          const isRunning = running?.version === r.version;
          return (
            <button
              key={r.version}
              onClick={() => select(r.version)}
              style={{
                display: 'block', width: '100%', textAlign: 'left',
                padding: '9px 14px 9px 16px',
                background: active ? '#2A3238' : 'transparent',
                border: 'none',
                borderLeft: active ? '2px solid #129271' : '2px solid transparent',
                borderBottom: '1px solid #252e35',
                cursor: 'pointer',
                color: active ? '#ECEFF1' : '#8b9aaa',
              }}
            >
              <div style={{ display: 'flex', alignItems: 'center', gap: 7 }}>
                <span style={{ fontSize: 12.5, fontWeight: active ? 600 : 500 }}>v{r.version}</span>
                {isRunning && (
                  <span
                    title="This is the release your console is running"
                    style={{
                      padding: '0 6px', borderRadius: 3, fontSize: 9, fontWeight: 700,
                      letterSpacing: '0.4px', color: '#129271', border: '1px solid #129271',
                    }}
                  >
                    RUNNING
                  </span>
                )}
              </div>
              <div style={{ fontSize: 10.5, color: '#556070', marginTop: 2, fontFamily: "'JetBrains Mono', monospace" }}>
                {r.date}
              </div>
            </button>
          );
        })}
      </aside>

      <main style={{ flex: 1, overflowY: 'auto', padding: '28px 40px', maxWidth: 780 }}>
        {/* A dev build is between releases, so the notes below are NOT a description of the running
            code. Say so rather than let it be read as one. */}
        {build && !build.isRelease && build.version !== 'dev' && (
          <div style={{
            marginBottom: 22, padding: '10px 14px', borderRadius: 6,
            border: '1px solid #f4a60d', color: '#f4a60d', fontSize: 12.5, lineHeight: 1.5,
          }}>
            This console is running <strong>{build.version}</strong> — a development build made after
            v{running?.version ?? releases[0].version} was released. The notes below cover that
            release; changes made since it are not listed here.
          </div>
        )}

        <div className="help-content">
          <ReactMarkdown>{current.content}</ReactMarkdown>
        </div>
      </main>
    </div>
  );
}
