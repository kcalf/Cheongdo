import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Aspire가 services__api__http__0을 넣어 주면 그 주소를, 아니면 로컬 기본 주소를 쓴다.
const api = process.env.services__api__http__0 ?? process.env.API_URL ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  server: {
    port: Number(process.env.PORT) || 5173,
    proxy: {
      '/api': { target: api, changeOrigin: true, rewrite: (p) => p.replace(/^\/api/, '') },
    },
  },
});
