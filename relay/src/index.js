// NodeRunner Online relay: one room per session code. The host's socket carries every guest's bytes, each message
// prefixed with [guest id: u32 LE][kind: u8] (0 = data, 1 = guest arrived, 2 = guest left / close this guest).
// A guest's socket carries its own bytes as is. The relay never reads them.
import { DurableObject } from "cloudflare:workers";

const PROTOCOL = "1";
const CODE_CHARS = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0, 1
const CODE = /^NRS-[A-HJ-NP-Z2-9]{4}$/;
const MAX_GUESTS = 8;
const HOST_GRACE_MS = 60_000;
const MAX_MESSAGE = 1 << 20;

// Close codes read by NodeRunner.
const ROOM_GONE = 4004; // no session with this code
const HOST_AWAY = 4003; // the host is reconnecting: try again
const HOST_LOST = 4001; // the host's link dropped: try again
const ENDED = 4002; // the host ended the session
const FULL = 4029;
const REPLACED = 4005;
const CLOSED_BY_HOST = 4006;
const TOO_BIG = 4009;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.headers.get("Upgrade") !== "websocket")
      return new Response("NodeRunner Online relay\n", { headers: { "content-type": "text/plain" } });
    if (url.searchParams.get("v") !== PROTOCOL)
      return new Response("Unsupported version\n", { status: 426 });

    if (url.pathname === "/host") {
      const code = url.searchParams.get("code");
      if (code !== null) {
        if (!CODE.test(code)) return new Response("Bad code\n", { status: 400 });
        return room(env, code).fetch(forward(request, code, { token: url.searchParams.get("token") ?? "" }));
      }
      for (let i = 0; i < 10; i++) {
        const fresh = newCode();
        const response = await room(env, fresh).fetch(forward(request, fresh, { new: "1" }));
        if (response.status !== 409) return response;
      }
      return new Response("No free code\n", { status: 503 });
    }

    const join = /^\/join\/([^/]+)$/.exec(url.pathname);
    if (join) {
      const code = decodeURIComponent(join[1]).toUpperCase();
      if (!CODE.test(code)) return new Response("Bad code\n", { status: 400 });
      return room(env, code).fetch(forward(request, code, {}, "/join"));
    }
    return new Response("Not found\n", { status: 404 });
  },
};

function room(env, code) {
  return env.ROOMS.get(env.ROOMS.idFromName(code));
}

function forward(request, code, params, path = "/host") {
  const url = new URL(path, "https://room");
  url.searchParams.set("code", code);
  for (const [k, v] of Object.entries(params)) url.searchParams.set(k, v);
  return new Request(url, request);
}

function newCode() {
  const bytes = crypto.getRandomValues(new Uint8Array(4));
  return "NRS-" + Array.from(bytes, (b) => CODE_CHARS[b % CODE_CHARS.length]).join("");
}

function token() {
  const bytes = crypto.getRandomValues(new Uint8Array(18));
  return btoa(String.fromCharCode(...bytes)).replace(/[+/=]/g, "");
}

function frame(id, kind, data) {
  const body = data ? new Uint8Array(data) : new Uint8Array(0);
  const out = new Uint8Array(5 + body.length);
  new DataView(out.buffer).setUint32(0, id, true);
  out[4] = kind;
  out.set(body, 5);
  return out;
}

function closeQuietly(ws, code, reason) {
  try {
    ws.close(code, reason);
  } catch {
    // already closed
  }
}

function sendQuietly(ws, data) {
  try {
    ws.send(data);
    return true;
  } catch {
    return false; // the link just died: its close event follows
  }
}

export class Room extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    // Keep-alive answered without waking the room.
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  // A refused socket: accepted then closed at once, so the client reads the reason.
  refuse(code, reason) {
    const [client, server] = Object.values(new WebSocketPair());
    this.ctx.acceptWebSocket(server, ["refused"]);
    server.serializeAttachment({ refused: true });
    server.close(code, reason);
    return new Response(null, { status: 101, webSocket: client });
  }

  live(tag, except) {
    return this.ctx.getWebSockets(tag).filter((ws) => ws !== except && ws.readyState === WebSocket.OPEN);
  }

  async fetch(request) {
    const url = new URL(request.url);
    const code = url.searchParams.get("code");
    const state = await this.ctx.storage.get("room");

    if (url.pathname === "/host") {
      let room = state;
      if (url.searchParams.get("new") === "1") {
        if (state) return new Response(null, { status: 409 });
        room = { code, token: token() };
        await this.ctx.storage.put("room", room);
      } else {
        if (!state || state.token !== url.searchParams.get("token")) return this.refuse(ROOM_GONE, "room gone");
        for (const old of this.live("host")) closeQuietly(old, REPLACED, "replaced");
      }
      await this.ctx.storage.deleteAlarm();
      const [client, server] = Object.values(new WebSocketPair());
      this.ctx.acceptWebSocket(server, ["host"]);
      server.serializeAttachment({ host: true });
      sendQuietly(server, JSON.stringify({ type: "room", code: room.code, token: room.token }));
      return new Response(null, { status: 101, webSocket: client });
    }

    if (!state) return this.refuse(ROOM_GONE, "room gone");
    const host = this.live("host")[0];
    if (!host) return this.refuse(HOST_AWAY, "host away");
    if (this.live("guest").length >= MAX_GUESTS) return this.refuse(FULL, "full");
    const id = ((await this.ctx.storage.get("next")) ?? 0) + 1;
    await this.ctx.storage.put("next", id);
    const [client, server] = Object.values(new WebSocketPair());
    this.ctx.acceptWebSocket(server, ["guest", "g" + id]);
    server.serializeAttachment({ id });
    sendQuietly(host, frame(id, 1));
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws, message) {
    if (typeof message === "string") return;
    if (message.byteLength > MAX_MESSAGE) {
      closeQuietly(ws, TOO_BIG, "message too big");
      return;
    }
    const who = ws.deserializeAttachment() ?? {};
    if (who.host) {
      if (message.byteLength < 5) return;
      const view = new DataView(message);
      const id = view.getUint32(0, true);
      const kind = view.getUint8(4);
      const guest = this.live("g" + id)[0];
      if (!guest) {
        if (kind === 0) sendQuietly(ws, frame(id, 2));
        return;
      }
      if (kind === 0) sendQuietly(guest, message.slice(5));
      else if (kind === 2) closeQuietly(guest, CLOSED_BY_HOST, "closed by host");
      return;
    }
    const host = this.live("host")[0];
    if (host) sendQuietly(host, frame(who.id, 0, message));
    else closeQuietly(ws, HOST_LOST, "host lost");
  }

  async webSocketClose(ws, code) {
    closeQuietly(ws, 1000, "bye");
    await this.left(ws, code === 1000);
  }

  async webSocketError(ws) {
    await this.left(ws, false);
  }

  async left(ws, clean) {
    const who = ws.deserializeAttachment() ?? {};
    if (who.refused) return;
    if (!who.host) {
      const host = this.live("host")[0];
      if (host) sendQuietly(host, frame(who.id, 2));
      return;
    }
    if (this.live("host", ws).length > 0) return; // replaced by a newer link of the host
    const guests = this.live("guest", ws);
    if (clean) {
      for (const g of guests) closeQuietly(g, ENDED, "ended");
      await this.ctx.storage.deleteAll();
      return;
    }
    for (const g of guests) closeQuietly(g, HOST_LOST, "host lost");
    await this.ctx.storage.setAlarm(Date.now() + HOST_GRACE_MS);
  }

  async alarm() {
    if (this.live("host").length > 0) return;
    for (const g of this.live("guest")) closeQuietly(g, ENDED, "ended");
    await this.ctx.storage.deleteAll();
  }
}
