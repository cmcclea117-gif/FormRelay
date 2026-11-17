import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  // `npm run build` drops the app where ASP.NET Core serves static files from.
  build: { outDir: '../src/FormRelay.Api/wwwroot', emptyOutDir: true },
  // `npm run dev` proxies API calls to the running ASP.NET Core process.
  server: { proxy: { '/api': 'http://localhost:5105' } },
})
