import type React from 'react'
import { ChevronDown, Undo2 } from 'lucide-react'
import type { LinkOption } from '../types'

/** What the user picked for one row; absent means automatic. */
export interface LinkPick { choice: string; gameId?: string | null }

export const isPicked = (o: LinkOption, pick: LinkPick | undefined) =>
  pick ? o.choice === pick.choice && (o.gameId ?? null) === (pick.gameId ?? null) : o.choice === 'auto'

interface Props {
  title: string
  options: LinkOption[]
  pick: LinkPick | undefined
  open: boolean
  onToggle: () => void
  onPick: (pick: LinkPick | undefined) => void
}

const TONE = { join: '#129271', new: '#6B7280', blocked: '#f4a60d', mine: '#60a5fa' } as const

function outcome(o: LinkOption): { text: string; tone: keyof typeof TONE } {
  if (o.kind === 'join') return { text: `Joins “${o.name}”`, tone: 'join' }
  if (o.kind === 'new') return { text: o.choice === 'separate' ? `Kept as its own game “${o.name}”` : `New game “${o.name}” on the server`, tone: 'new' }
  return { text: 'Can’t be added under any free name', tone: 'blocked' }
}

const btn: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: 5, padding: '4px 10px 4px 8px',
  background: 'transparent', border: '1px solid #494949', borderRadius: 4,
  color: '#ECEFF1', fontSize: 11.5, cursor: 'pointer', fontFamily: 'inherit',
}

/**
 * Which server game an emulator save joins: one line saying what will happen, and a Change button that opens
 * the choices for this row only. Anything but the automatic choice marks the row "chosen by you", with Back to
 * automatic beside it.
 */
export function ServerGameLink({ title, options, pick, open, onToggle, onPick }: Props) {
  const current = options.find(o => isPicked(o, pick)) ?? options[0]
  const o = outcome(current)
  const mine = !!pick
  return (
    <div style={{ marginTop: 6, display: 'flex', flexDirection: 'column', gap: 6 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', fontSize: 11.5, color: '#9CA3AF' }}>
        <span aria-hidden="true" style={{ width: 7, height: 7, borderRadius: '50%', background: TONE[mine ? 'mine' : o.tone] }} />
        <span>{o.text}</span>
        {mine && <span style={{ color: TONE.mine }}>· chosen by you</span>}
        <span style={{ marginLeft: 'auto', display: 'inline-flex', gap: 6 }}>
          {mine && !open && (
            <button type="button" style={{ ...btn, border: 'none', color: TONE.mine }} onClick={() => onPick(undefined)}>
              <Undo2 size={13} aria-hidden="true" />
              Back to automatic
            </button>
          )}
          <button
            type="button"
            style={btn}
            aria-expanded={open}
            aria-label={`${open ? 'Close' : 'Change'} the server game for ${title}`}
            onClick={onToggle}
          >
            {open ? 'Close' : 'Change'}
            <ChevronDown size={13} aria-hidden="true" style={{ transform: open ? 'rotate(180deg)' : undefined }} />
          </button>
        </span>
      </div>
      {open && (
        <div role="radiogroup" aria-label={`Server game for ${title}`}
          style={{ background: '#171e23', border: '1px solid #494949', borderRadius: 6, padding: 4, display: 'flex', flexDirection: 'column', gap: 2 }}>
          {options.map(x => {
            const on = isPicked(x, pick)
            const blocked = x.kind === 'blocked'
            const label = x.choice === 'auto'
              ? `Automatic: ${x.kind === 'join' ? `join “${x.name}”` : x.kind === 'new' ? `new game “${x.name}”` : x.name}`
              : x.choice === 'separate' ? 'Keep it as its own game' : x.name
            const tone = blocked ? TONE.blocked : x.kind === 'join' ? TONE.join : TONE.new
            return (
              <button
                key={`${x.choice}:${x.gameId ?? ''}`}
                type="button"
                role="radio"
                aria-checked={on}
                aria-disabled={blocked || undefined}
                onClick={() => { if (!blocked) onPick(x.choice === 'auto' ? undefined : { choice: x.choice, gameId: x.gameId }) }}
                style={{
                  display: 'flex', flexDirection: 'column', gap: 3, textAlign: 'left', fontFamily: 'inherit',
                  fontSize: 12, color: blocked ? '#6B7280' : '#ECEFF1', background: on ? 'rgba(18,146,113,0.12)' : 'transparent',
                  border: 0, borderRadius: 4, padding: '7px 9px', cursor: blocked ? 'not-allowed' : 'pointer',
                }}
              >
                <span style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                  <span aria-hidden="true" style={{
                    width: 9, height: 9, borderRadius: '50%', border: `2px ${blocked ? 'dashed' : 'solid'} ${on ? '#129271' : '#6B7280'}`,
                    background: on ? '#129271' : 'transparent',
                  }} />
                  <b style={{ fontWeight: 600 }}>{label}</b>
                  <span style={{ fontSize: 10, padding: '1px 6px', borderRadius: 3, color: tone, border: `1px solid ${tone}55` }}>{x.badge}</span>
                </span>
                <span style={{ color: '#9CA3AF', fontSize: 11, paddingLeft: 17 }}>{x.detail}</span>
              </button>
            )
          })}
          {options.length === 1 && (
            <span style={{ color: '#9CA3AF', fontSize: 11, padding: '6px 9px 6px 26px' }}>
              No other server game has this title or keeps these files, so there’s nothing else to link it to.
            </span>
          )}
        </div>
      )}
    </div>
  )
}
