import { Card } from '../ui';

/** The four ways a page arranges boxes: block flow, flex row, wrapping, grid. */
export function Layout() {
  return (
    <div className="flex flex-col gap-4">
      <Card title="Block flow" note="No display, no flex - boxes stack and fill the width">
        <p className="text-sm">
          A paragraph with <strong>bold</strong>, <em>italic</em> and a{' '}
          <span className="rounded bg-accent-soft px-1.5 py-0.5 text-xs text-accent">badge</span> in the middle of
          the sentence.
        </p>
        <p className="mt-2 text-sm text-muted">
          A second paragraph, separated by a margin rather than a gap.
        </p>
      </Card>

      <Card title="Flex row" note="justify-between, items-center, gap">
        <div className="flex items-center justify-between gap-3">
          <span className="text-sm">Left</span>
          <span className="h-px flex-1 bg-hairline" />
          <span className="text-sm text-muted">Right</span>
        </div>
      </Card>

      <Card title="Wrapping" note="flex-wrap with a fixed basis">
        <div className="flex flex-wrap gap-2">
          {['alpha', 'beta', 'gamma', 'delta', 'epsilon', 'zeta', 'eta'].map((word) => (
            <span key={word} className="rounded-md bg-raised px-2.5 py-1 text-xs">
              {word}
            </span>
          ))}
        </div>
      </Card>

      <Card title="Grid" note="grid-cols-3 with a gap">
        <div className="grid grid-cols-3 gap-2">
          {Array.from({ length: 6 }, (_, i) => (
            <div key={i} className="rounded-md bg-raised py-4 text-center text-sm">
              {i + 1}
            </div>
          ))}
        </div>
      </Card>

      <Card title="Centred" note="mx-auto on a fixed width">
        <div className="mx-auto w-40 rounded-md bg-accent-soft py-3 text-center text-sm text-accent">
          auto margins
        </div>
      </Card>
    </div>
  );
}
