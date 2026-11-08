# NodeRunner Online relay

Cloudflare Worker + Durable Object that links the computers of a NodeRunner Online session. One room per session code
(`NRS-XXXX`); the relay forwards bytes and never reads them. Free plan is enough (SQLite Durable Objects, WebSocket
hibernation).

```
npm install
npx wrangler dev      # local: ws://127.0.0.1:8787
npx wrangler deploy   # online: wss://noderunner-relay.<subdomain>.workers.dev
```

NodeRunner uses `OnlineController.DefaultRelay`, or `OnlineRelay` in `settings.json`, or the `NODERUNNER_RELAY`
environment variable. The session test against a relay runs when `NODERUNNER_RELAY` is set:

```
NODERUNNER_RELAY=ws://127.0.0.1:8787 dotnet test --filter OnlineRelay
```

Endpoints: `/host?v=1` (new room; `&code=...&token=...` to come back after a drop), `/join/NRS-XXXX?v=1`.
Close codes: 4001 host lost, 4002 ended, 4003 host away, 4004 no such room, 4005 replaced, 4006 closed by host,
4009 message too big, 4029 full.
