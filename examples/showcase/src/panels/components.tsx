import { useState } from 'react';
import { Button, Card } from '../ui';

export function Components() {
  const [text, setText] = useState('');
  const [on, setOn] = useState(true);
  const [pick, setPick] = useState('b');
  const [clicks, setClicks] = useState(0);

  return (
    <div className="flex flex-col gap-4">
      <Card title="Buttons" note="Three tones, one component">
        <div className="flex flex-wrap gap-2">
          <Button onClick={() => setClicks(clicks + 1)}>Default</Button>
          <Button tone="accent" onClick={() => setClicks(clicks + 1)}>
            Accent
          </Button>
          <Button tone="danger" onClick={() => setClicks(0)}>
            Reset
          </Button>
        </div>
        <p className="mt-3 text-xs text-muted">{clicks} clicks</p>
      </Card>

      <Card title="Text field" note="Controlled: React owns the value">
        <input
          value={text}
          onInput={(e) => setText((e.target as HTMLInputElement).value)}
          placeholder="type something"
          className="rounded-lg border border-hairline bg-sunken px-3 py-2 text-sm"
        />
        <p className="mt-2 text-xs text-muted">{text.length} characters</p>
      </Card>

      <Card title="Toggle" note="A checkbox that drives a class">
        <label className="flex items-center gap-3">
          <input type="checkbox" checked={on} onChange={() => setOn(!on)} />
          <span className={on ? 'text-sm text-good' : 'text-sm text-muted'}>{on ? 'enabled' : 'disabled'}</span>
        </label>
      </Card>

      <Card title="Segmented" note="One of three, styled by state">
        <div className="flex gap-1 rounded-lg bg-sunken p-1">
          {['a', 'b', 'c'].map((id) => (
            <button
              key={id}
              onClick={() => setPick(id)}
              className={
                'flex-1 rounded-md py-1.5 text-xs ' + (pick === id ? 'bg-accent text-white' : 'text-muted')
              }
            >
              option {id}
            </button>
          ))}
        </div>
      </Card>

      <Card title="Progress" note="A width in percent, animated by state">
        <div className="h-2 overflow-hidden rounded-full bg-sunken">
          <div className="h-full rounded-full bg-accent" style={{ width: `${(clicks % 10) * 10 + 10}%` }} />
        </div>
      </Card>
    </div>
  );
}
