import { useEffect, useState } from 'react';
import { Button, Card } from '../ui';

/** A handler the mod never registered throws. Catching it keeps one missing name from blanking the panel. */
function ask(name: string, argument: string) {
  try {
    return s1.call(name, argument);
  } catch (error) {
    return String(error);
  }
}

/** Everything the page can reach outside its own document: the mod, storage, the phone. */
export function Bridge() {
  const [answer, setAnswer] = useState('-');
  const [note, setNote] = useState(() => s1.storage.get('note', ''));
  const [ticks, setTicks] = useState(0);

  // A timer keeps running across re-renders; the cleanup is what stops it when the tab changes.
  useEffect(() => {
    const id = setInterval(() => setTicks((t) => t + 1), 1000);
    return () => clearInterval(id);
  }, []);

  return (
    <div className="flex flex-col gap-4">
      <Card title="Ask the mod" note="s1.call is synchronous: the answer is the return value">
        <Button tone="accent" onClick={() => setAnswer(ask('hello', 'showcase'))}>
          call hello
        </Button>
        <p className="mt-3 rounded-lg bg-sunken px-3 py-2 text-xs text-muted">{answer}</p>
      </Card>

      <Card title="Storage" note="One string store per app, kept across restarts">
        <input
          value={note}
          onInput={(e) => {
            const next = (e.target as HTMLInputElement).value;
            setNote(next);
            s1.storage.set('note', next);
          }}
          placeholder="survives a restart"
          className="rounded-lg border border-hairline bg-sunken px-3 py-2 text-sm"
        />
      </Card>

      <Card title="Timer" note="setInterval, cleared by the effect's cleanup">
        <p className="text-2xl tabular-nums">{ticks}s</p>
      </Card>

      <Card title="Phone" note="Rotating keeps the document and the script">
        <div className="flex gap-2">
          <Button onClick={() => s1.setOrientation('portrait')}>portrait</Button>
          <Button onClick={() => s1.setOrientation('landscape')}>landscape</Button>
        </div>
        <p className="mt-3 text-xs text-muted">
          app id <span className="text-ink">{s1.appId}</span>, now {s1.orientation}
        </p>
      </Card>
    </div>
  );
}
