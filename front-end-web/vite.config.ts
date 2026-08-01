import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    // Bind on all interfaces, not just loopback.
    //
    // Needed for RECORDING ONLY. LiveKit's egress worker loads /recorder in headless Chrome to
    // capture it, and that worker lives inside the Docker Desktop VM — a different machine as
    // far as the network is concerned. On loopback the dev server is invisible to it, and the
    // egress fails with nothing to render.
    //
    // TRADE-OFF: this also exposes the dev server to your LAN. Fine on a laptop behind a home
    // router; drop it if you are ever on an untrusted network.
    host: true,
    // Vite blocks requests whose Host header it does not recognise. Egress reaches us as
    // host.docker.internal, which would otherwise be answered with "Blocked request" — a plain
    // 403 that looks nothing like a networking problem and is easy to spend an hour on.
    allowedHosts: ['host.docker.internal', 'localhost', '127.0.0.1'],
    proxy: {
      '/api': {
        target: 'http://localhost',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://localhost',
        ws: true,
        changeOrigin: true,
      }
    }
  }
})