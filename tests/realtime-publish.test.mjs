import http from "node:http";
import { Buffer } from "node:buffer";
import * as esbuild from "esbuild";

function readBody(req) {
  return new Promise((resolve, reject) => {
    let body = "";
    req.setEncoding("utf8");
    req.on("data", (chunk) => {
      body += chunk;
    });
    req.on("end", () => resolve(body));
    req.on("error", reject);
  });
}

function listen(server) {
  return new Promise((resolve, reject) => {
    server.on("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
}

function close(server) {
  return new Promise((resolve) => server.close(resolve));
}

async function bundleWorker() {
  const result = await esbuild.build({
    entryPoints: ["worker/src/index.ts"],
    bundle: true,
    platform: "neutral",
    format: "esm",
    write: false,
    plugins: [
      {
        name: "cloudflare-workers-stub",
        setup(build) {
          build.onResolve({ filter: /^cloudflare:workers$/ }, (args) => ({
            path: args.path,
            namespace: "cf-stub"
          }));
          build.onLoad({ filter: /.*/, namespace: "cf-stub" }, () => ({
            loader: "js",
            contents:
              "export class DurableObject { constructor(ctx, env) { this.ctx = ctx; this.env = env; } }"
          }));
        }
      }
    ]
  });

  const code = result.outputFiles[0].text;
  return import(`data:text/javascript;base64,${Buffer.from(code).toString("base64")}`);
}

function createMockRealtimeApi(captured) {
  return http.createServer(async (req, res) => {
    try {
      const rawBody = await readBody(req);
      const parsedBody = rawBody ? JSON.parse(rawBody) : undefined;
      captured.push({
        method: req.method,
        url: req.url,
        headers: req.headers,
        rawBody,
        body: parsedBody
      });

      if (req.method === "POST" && req.url === "/apps/test-app/sessions/new") {
        res.writeHead(201, { "content-type": "application/json" });
        res.end(JSON.stringify({ sessionId: "session-1" }));
        return;
      }

      if (req.method === "POST" && req.url === "/apps/test-app/sessions/session-1/tracks/new") {
        const sdp = parsedBody?.sessionDescription?.sdp;
        if (typeof sdp !== "string" || !sdp.endsWith("\r\n")) {
          res.writeHead(400, { "content-type": "application/json" });
          res.end(
            JSON.stringify({
              errorCode: "missing_termination",
              errorDescription: JSON.stringify(sdp?.slice(-12))
            })
          );
          return;
        }

        res.writeHead(200, { "content-type": "application/json" });
        res.end(
          JSON.stringify({
            sessionDescription: { type: "answer", sdp: "v=0\r\n" },
            tracks: [
              {
                location: "local",
                mid: parsedBody.tracks[0].mid,
                trackName: parsedBody.tracks[0].trackName
              }
            ]
          })
        );
        return;
      }

      res.writeHead(404, { "content-type": "application/json" });
      res.end(JSON.stringify({ error: `unexpected ${req.method} ${req.url}` }));
    } catch (error) {
      res.writeHead(500, { "content-type": "application/json" });
      res.end(JSON.stringify({ error: error.message }));
    }
  });
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

const captured = [];
const mock = createMockRealtimeApi(captured);
await listen(mock);

try {
  const { port } = mock.address();
  const worker = await bundleWorker();
  const response = await worker.default.fetch(
    new Request("https://worker.test/api/realtime/publish", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        room: "room-a",
        mid: "0",
        trackName: "mic-test",
        type: "offer",
        sdp: "v=0\no=- 1 2 IN IP4 127.0.0.1\ns=-\nt=0 0"
      })
    }),
    {
      REALTIME_SFU_APP_ID: "test-app",
      REALTIME_SFU_BEARER_TOKEN: "test-token",
      SFU_API_BASE: `http://127.0.0.1:${port}`,
      ASSETS: { fetch: () => new Response("asset", { status: 404 }) },
      ROOMS: {}
    }
  );

  const body = await response.json();
  assert(response.ok, `Worker publish returned ${response.status}: ${JSON.stringify(body)}`);

  const sessionCall = captured.find((call) => call.url === "/apps/test-app/sessions/new");
  const tracksCall = captured.find((call) => call.url === "/apps/test-app/sessions/session-1/tracks/new");
  assert(sessionCall, "Missing sessions/new call");
  assert(tracksCall, "Missing tracks/new call");
  assert(sessionCall.rawBody === "", `sessions/new unexpectedly had a body: ${sessionCall.rawBody}`);
  assert(!sessionCall.headers["content-type"], `sessions/new unexpectedly had content-type`);

  const forwardedSdp = tracksCall.body.sessionDescription.sdp;
  const expected = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n";
  assert(forwardedSdp === expected, `Forwarded SDP mismatch: ${JSON.stringify(forwardedSdp)}`);

  console.log("realtime publish worker test passed");
} finally {
  await close(mock);
}
