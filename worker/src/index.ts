import { DurableObject } from "cloudflare:workers";

export interface Env {
  ASSETS: Fetcher;
  ROOMS: DurableObjectNamespace<RoomDurableObject>;
  ROOM_TOKEN?: string;
  REALTIME_SFU_APP_ID?: string;
  REALTIME_SFU_BEARER_TOKEN?: string;
  SFU_API_BASE?: string;
}

type PeerRole = "source" | "client" | "monitor" | "adapter";

interface PeerAttachment {
  id: string;
  role: PeerRole;
  connectedAt: number;
  userAgent: string;
}

function json(data: unknown, init: ResponseInit = {}): Response {
  const headers = new Headers(init.headers);
  headers.set("content-type", "application/json; charset=utf-8");

  return new Response(JSON.stringify(data), {
    ...init,
    headers
  });
}

function parseRole(value: string | null): PeerRole {
  if (value === "source" || value === "client" || value === "monitor" || value === "adapter") {
    return value;
  }
  return "client";
}

function isWebSocket(request: Request): boolean {
  return request.headers.get("upgrade")?.toLowerCase() === "websocket";
}

function getRealtimeApiBase(env: Env): string {
  return env.SFU_API_BASE || "https://rtc.live.cloudflare.com/v1";
}

function requireRealtimeConfig(env: Env): Response | undefined {
  if (!env.REALTIME_SFU_APP_ID || !env.REALTIME_SFU_BEARER_TOKEN) {
    return json(
      {
        error: "Realtime SFU is not configured. Set REALTIME_SFU_APP_ID and REALTIME_SFU_BEARER_TOKEN."
      },
      { status: 501 }
    );
  }
  return undefined;
}

function validateRoomToken(env: Env, token: unknown): Response | undefined {
  if (env.ROOM_TOKEN && token !== env.ROOM_TOKEN) {
    return json({ error: "Invalid room token." }, { status: 401 });
  }
  return undefined;
}

async function readJsonRecord(request: Request): Promise<Record<string, unknown>> {
  const parsed = await request.json();
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error("Expected a JSON object.");
  }
  return parsed as Record<string, unknown>;
}

function asString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value.trim() : undefined;
}

function normalizeRealtimeSdp(value: unknown): string | undefined {
  if (typeof value !== "string" || !value.trim()) {
    return undefined;
  }

  let sdp = value.replace(/\r?\n/g, "\r\n").replace(/\r(?!\n)/g, "\r\n");
  if (!sdp.endsWith("\r\n")) {
    sdp += "\r\n";
  }
  return sdp;
}

function findTrackError(data: unknown): unknown | undefined {
  if (!data || typeof data !== "object" || !Array.isArray((data as { tracks?: unknown }).tracks)) {
    return undefined;
  }

  return (data as { tracks: Array<Record<string, unknown>> }).tracks.find(
    (track) => track.errorCode || track.errorDescription
  );
}

function findRealtimeError(data: unknown): unknown | undefined {
  if (!data || typeof data !== "object") {
    return undefined;
  }

  if ((data as { errorCode?: unknown }).errorCode || (data as { errorDescription?: unknown }).errorDescription) {
    return data;
  }

  return findTrackError(data);
}

async function realtimeApi(
  env: Env,
  operation: string,
  path: string,
  init: RequestInit = {}
): Promise<{ ok: true; data: unknown } | { ok: false; response: Response }> {
  const endpoint = `${getRealtimeApiBase(env)}${path}`;
  const headers = new Headers(init.headers);
  headers.set("authorization", `Bearer ${env.REALTIME_SFU_BEARER_TOKEN}`);
  if (init.body !== undefined && init.body !== null && !headers.has("content-type")) {
    headers.set("content-type", "application/json");
  }

  const response = await fetch(endpoint, {
    ...init,
    headers
  });

  const text = await response.text();
  let data: unknown = {};
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = { raw: text };
    }
  }

  if (!response.ok) {
    return {
      ok: false,
      response: json(
        {
          error: "Cloudflare Realtime API request failed.",
          operation,
          status: response.status,
          endpoint: path,
          details: data
        },
        { status: 502 }
      )
    };
  }

  const realtimeError = findRealtimeError(data);
  if (realtimeError) {
    return {
      ok: false,
      response: json(
        {
          error: "Cloudflare Realtime API operation failed.",
          operation,
          status: response.status,
          endpoint: path,
          details: realtimeError
        },
        { status: 502 }
      )
    };
  }

  return { ok: true, data };
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === "/health") {
      return json({ ok: true });
    }

    if (url.pathname === "/api/config") {
      return json({
        realtimeEnabled: Boolean(env.REALTIME_SFU_APP_ID && env.REALTIME_SFU_BEARER_TOKEN),
        roomTokenRequired: Boolean(env.ROOM_TOKEN)
      });
    }

    if (url.pathname === "/api/realtime/publish" && request.method === "POST") {
      const configError = requireRealtimeConfig(env);
      if (configError) {
        return configError;
      }

      let body: Record<string, unknown>;
      try {
        body = await readJsonRecord(request);
      } catch (error) {
        return json({ error: (error as Error).message }, { status: 400 });
      }

      const room = asString(body.room);
      const sdp = normalizeRealtimeSdp(body.sdp);
      const type = asString(body.type) || "offer";
      const mid = asString(body.mid);
      const trackName = asString(body.trackName) || `mic-${crypto.randomUUID()}`;
      if (!room || !sdp || !mid) {
        return json({ error: "Missing room, sdp, or mid." }, { status: 400 });
      }

      const tokenError = validateRoomToken(env, body.token);
      if (tokenError) {
        return tokenError;
      }

      const appId = encodeURIComponent(env.REALTIME_SFU_APP_ID!);
      const session = await realtimeApi(env, "create-session", `/apps/${appId}/sessions/new`, {
        method: "POST"
      });
      if (!session.ok) {
        return session.response;
      }

      const sessionId = asString((session.data as Record<string, unknown>).sessionId);
      if (!sessionId) {
        return json({ error: "Realtime API did not return a sessionId.", details: session.data }, { status: 502 });
      }

      const tracks = await realtimeApi(env, "publish-track", `/apps/${appId}/sessions/${encodeURIComponent(sessionId)}/tracks/new`, {
        method: "POST",
        body: JSON.stringify({
          sessionDescription: { type, sdp },
          tracks: [{ location: "local", mid, trackName, kind: "audio" }]
        })
      });
      if (!tracks.ok) {
        return tracks.response;
      }

      return json({
        sessionId,
        trackName,
        ...(tracks.data as Record<string, unknown>)
      });
    }

    if (url.pathname === "/api/realtime/adapter/start" && request.method === "POST") {
      const configError = requireRealtimeConfig(env);
      if (configError) {
        return configError;
      }

      let body: Record<string, unknown>;
      try {
        body = await readJsonRecord(request);
      } catch (error) {
        return json({ error: (error as Error).message }, { status: 400 });
      }

      const room = asString(body.room);
      const sessionId = asString(body.sessionId);
      const trackName = asString(body.trackName);
      if (!room || !sessionId || !trackName) {
        return json({ error: "Missing room, sessionId, or trackName." }, { status: 400 });
      }

      const tokenError = validateRoomToken(env, body.token);
      if (tokenError) {
        return tokenError;
      }

      if (url.protocol !== "https:") {
        return json(
          {
            error: "Realtime WebSocket adapter requires a deployed HTTPS Worker URL so Cloudflare can connect back with wss://."
          },
          { status: 400 }
        );
      }

      const endpoint = new URL("/ws", request.url);
      endpoint.protocol = "wss:";
      endpoint.searchParams.set("room", room);
      endpoint.searchParams.set("role", "adapter");
      if (env.ROOM_TOKEN) {
        endpoint.searchParams.set("token", env.ROOM_TOKEN);
      }

      const appId = encodeURIComponent(env.REALTIME_SFU_APP_ID!);
      const adapter = await realtimeApi(env, "start-websocket-adapter", `/apps/${appId}/adapters/websocket/new`, {
        method: "POST",
        body: JSON.stringify({
          tracks: [
            {
              location: "remote",
              sessionId,
              trackName,
              endpoint: endpoint.toString(),
              outputCodec: "pcm",
              mode: "stream"
            }
          ]
        })
      });
      if (!adapter.ok) {
        return adapter.response;
      }

      return json(adapter.data);
    }

    if (url.pathname === "/api/realtime/adapter/close" && request.method === "POST") {
      const configError = requireRealtimeConfig(env);
      if (configError) {
        return configError;
      }

      let body: Record<string, unknown>;
      try {
        body = await readJsonRecord(request);
      } catch (error) {
        return json({ error: (error as Error).message }, { status: 400 });
      }

      const adapterId = asString(body.adapterId);
      if (!adapterId) {
        return json({ error: "Missing adapterId." }, { status: 400 });
      }

      const tokenError = validateRoomToken(env, body.token);
      if (tokenError) {
        return tokenError;
      }

      const appId = encodeURIComponent(env.REALTIME_SFU_APP_ID!);
      const closed = await realtimeApi(env, "close-websocket-adapter", `/apps/${appId}/adapters/websocket/close`, {
        method: "POST",
        body: JSON.stringify({ tracks: [{ adapterId }] })
      });

      if (!closed.ok) {
        return closed.response;
      }

      return json(closed.data);
    }

    if (url.pathname === "/ws") {
      if (!isWebSocket(request)) {
        return json({ error: "Expected a WebSocket upgrade request." }, { status: 426 });
      }

      const room = url.searchParams.get("room")?.trim();
      if (!room) {
        return json({ error: "Missing room query parameter." }, { status: 400 });
      }

      if (env.ROOM_TOKEN) {
        const token = url.searchParams.get("token");
        if (token !== env.ROOM_TOKEN) {
          return json({ error: "Invalid room token." }, { status: 401 });
        }
      }

      const id = env.ROOMS.idFromName(room);
      return env.ROOMS.get(id).fetch(request);
    }

    return env.ASSETS.fetch(request);
  }
};

export class RoomDurableObject extends DurableObject<Env> {
  async fetch(request: Request): Promise<Response> {
    if (!isWebSocket(request)) {
      return json({ error: "Expected a WebSocket upgrade request." }, { status: 426 });
    }

    const url = new URL(request.url);
    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    const attachment: PeerAttachment = {
      id: crypto.randomUUID(),
      role: parseRole(url.searchParams.get("role")),
      connectedAt: Date.now(),
      userAgent: request.headers.get("user-agent") ?? ""
    };

    server.serializeAttachment(attachment);
    this.ctx.acceptWebSocket(server);
    this.broadcastPeerCount();

    server.send(JSON.stringify({
      type: "ready",
      id: attachment.id,
      role: attachment.role,
      peerCount: this.ctx.getWebSockets().length
    }));

    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(sender: WebSocket, message: string | ArrayBuffer): Promise<void> {
    const senderInfo = this.getAttachment(sender);
    if (!senderInfo) {
      sender.close(1011, "Missing session attachment.");
      return;
    }

    if (typeof message === "string") {
      this.handleText(sender, senderInfo, message);
      return;
    }

    if (senderInfo.role !== "source" && senderInfo.role !== "adapter") {
      return;
    }

    for (const peer of this.ctx.getWebSockets()) {
      if (peer === sender) {
        continue;
      }

      const peerInfo = this.getAttachment(peer);
      if (peerInfo?.role === "client" || peerInfo?.role === "monitor") {
        peer.send(message);
      }
    }
  }

  async webSocketClose(): Promise<void> {
    this.broadcastPeerCount();
  }

  async webSocketError(): Promise<void> {
    this.broadcastPeerCount();
  }

  private handleText(sender: WebSocket, senderInfo: PeerAttachment, raw: string): void {
    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch {
      sender.send(JSON.stringify({ type: "error", error: "Text messages must be JSON." }));
      return;
    }

    const message = parsed as { type?: unknown };
    if (message.type === "ping") {
      sender.send(JSON.stringify({ type: "pong", now: Date.now() }));
      return;
    }

    if (senderInfo.role === "source" && message.type === "source-status") {
      this.broadcastJson(sender, {
        type: "source-status",
        sourceId: senderInfo.id,
        data: parsed,
        at: Date.now()
      });
    }
  }

  private broadcastPeerCount(): void {
    const sockets = this.ctx.getWebSockets();
    const counts = { source: 0, client: 0, monitor: 0, adapter: 0 };
    for (const socket of sockets) {
      const info = this.getAttachment(socket);
      if (info) {
        counts[info.role] += 1;
      }
    }

    const payload = JSON.stringify({
      type: "peers",
      count: sockets.length,
      roles: counts,
      at: Date.now()
    });

    for (const socket of sockets) {
      socket.send(payload);
    }
  }

  private broadcastJson(sender: WebSocket, data: unknown): void {
    const payload = JSON.stringify(data);
    for (const peer of this.ctx.getWebSockets()) {
      if (peer !== sender) {
        peer.send(payload);
      }
    }
  }

  private getAttachment(socket: WebSocket): PeerAttachment | undefined {
    return socket.deserializeAttachment() as PeerAttachment | undefined;
  }
}
