import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// Split from vite.config.ts because vitest ships its own bundled `vite`
// version whose types clash with the workspace one when both surfaces try
// to share a single config file.
export default defineConfig({
    plugins: [react()],
    test: {
        environment: 'jsdom',
        globals: true,
        setupFiles: ['./src/test/setup.ts'],
    },
});
