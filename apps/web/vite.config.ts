import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// In dev the web container talks to the api container via the compose
// network (`http://api:8080`). Outside compose, the fallback is the
// host-side API port bump (8081). Set VITE_API_BASE to override.
const API_TARGET = process.env.VITE_API_TARGET ?? 'http://api:8080';

// https://vite.dev/config/
export default defineConfig({
    plugins: [react(), tailwindcss()],
    server: {
        proxy: {
            '/api/v1':  { target: API_TARGET, changeOrigin: true },
            '/openapi': { target: API_TARGET, changeOrigin: true },
            '/health':  { target: API_TARGET, changeOrigin: true },
        },
    },
});
