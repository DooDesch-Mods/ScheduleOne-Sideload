import { defineConfig } from 'vite';
import tailwindcss from '@tailwindcss/vite';
import { sideload } from '@doodesch/sideload-vite';

// `gate` runs the engine's own CSS report over the built stylesheet and fails the build on anything NEW - the
// same parser and cascade the game uses, so it cannot drift from what actually renders. `baseline` is what this
// app already accepts losing; delete a line from it and the build tells you if it came back.
export default defineConfig({
  plugins: [
    tailwindcss(),
    sideload({
      appId: 'showcase',
      gate: '../../../Workspace/Tests/Sideload.Tests',
      baseline: 'showcase.baseline',
    }),
  ],
});
