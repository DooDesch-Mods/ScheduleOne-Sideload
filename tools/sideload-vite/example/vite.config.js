import { defineConfig } from 'vite';
import { sideload } from '../index.js';

export default defineConfig({
  plugins: [sideload({ appId: 'example' })],
});
