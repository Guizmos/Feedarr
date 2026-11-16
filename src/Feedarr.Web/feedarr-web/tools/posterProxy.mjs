import http from "node:http";
import https from "node:https";

// Run after config merging, including the local launcher's server override.
export function posterKeepAliveProxy() {
  let agent;
  return {
    name: "feedarr-poster-keep-alive",
    configResolved(config) {
      const api = config.server.proxy?.["/api"];
      if (!api) return;
      const options = typeof api === "string" ? { target: api } : api;
      if (!options.target) return;
      const Agent = String(options.target).startsWith("https:") ? https.Agent : http.Agent;
      agent = new Agent({ keepAlive: true, maxSockets: 16 });
      config.server.proxy = { "/api/posters": { ...options, agent }, ...config.server.proxy };
    },
    closeBundle() { agent?.destroy(); },
  };
}
