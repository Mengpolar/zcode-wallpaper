// Mock CDP server: pretends to be ZCode's debug endpoint for injector testing.
// Usage: node mock-cdp.mjs <port> <logfile>
import http from "node:http";
import crypto from "node:crypto";
import { appendFileSync } from "node:fs";

const port = Number(process.argv[2] || 19801);
const logFile = process.argv[3] || "mock-cdp.log";
const log = (s) => appendFileSync(logFile, new Date().toISOString().slice(11, 23) + " " + s + "\n");

const server = http.createServer((req, res) => {
  if (req.url === "/json/version") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ Browser: "mock/1.0", Protocol: "1.3" }));
  } else if (req.url === "/json/list") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify([
      {
        description: "",
        id: "mockpage1",
        title: "ZCode",
        type: "page",
        url: "file:///mock/renderer/index.html",
        webSocketDebuggerUrl: `ws://127.0.0.1:${port}/devtools/page/mockpage1`,
      },
    ]));
  } else {
    res.writeHead(404);
    res.end();
  }
});

// minimal WebSocket server (RFC6455: accept, parse text frames, reply {"id":n,"result":{}})
server.on("upgrade", (req, socket) => {
  const key = req.headers["sec-websocket-key"];
  const accept = crypto
    .createHash("sha1")
    .update(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")
    .digest("base64");
  socket.write(
    "HTTP/1.1 101 Switching Protocols\r\n" +
      "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
      `Sec-WebSocket-Accept: ${accept}\r\n\r\n`
  );
  log("WS connected");
  let buf = Buffer.alloc(0);
  socket.on("data", (chunk) => {
    buf = Buffer.concat([buf, chunk]);
    while (true) {
      if (buf.length < 2) break;
      const fin = buf[0] & 0x80;
      const opcode = buf[0] & 0x0f;
      let len = buf[1] & 0x7f;
      let off = 2;
      if (len === 126) {
        if (buf.length < 4) break;
        len = buf.readUInt16BE(2); off = 4;
      } else if (len === 127) {
        if (buf.length < 10) break;
        len = Number(buf.readBigUInt64BE(2)); off = 10;
      }
      const masked = (buf[1] & 0x80) !== 0;
      let maskKey = null;
      if (masked) { maskKey = buf.slice(off, off + 4); off += 4; }
      if (buf.length < off + len) break; // wait for more TCP data
      let payload = Buffer.from(buf.slice(off, off + len));
      buf = buf.slice(off + len);
      if (maskKey) {
        for (let j = 0; j < payload.length; j++) payload[j] ^= maskKey[j % 4];
      }
      if (opcode === 0x8) { log("WS close"); socket.end(); return; }
      if (opcode === 0x1) {
        handleText(socket, payload.toString("utf8"));
      }
    }
  });
});

function handleText(socket, text) {
  try {
    const msg = JSON.parse(text);
    if (msg.method) {
      const src = msg.params && msg.params.source ? msg.params.source : "";
      log(`CDP ${msg.method} (id=${msg.id}, ${text.length} bytes${src ? ", source " + src.length + " chars, head=" + src.slice(0, 60).replace(/\n/g, " ") : ""})`);
      const reply = JSON.stringify({ id: msg.id, result: {} });
      sendFrame(socket, reply);
    }
  } catch (e) {
    log("parse error: " + e.message);
  }
}

function sendFrame(socket, text) {
  const payload = Buffer.from(text, "utf8");
  const mask = crypto.randomBytes(4); // server->client unmasked per RFC, but Chromium client accepts both; keep unmasked
  let header;
  if (payload.length < 126) {
    header = Buffer.from([0x81, payload.length]);
  } else if (payload.length < 65536) {
    header = Buffer.alloc(4);
    header[0] = 0x81; header[1] = 126; header.writeUInt16BE(payload.length, 2);
  } else {
    header = Buffer.alloc(10);
    header[0] = 0x81; header[1] = 127; header.writeBigUInt64BE(BigInt(payload.length), 2);
  }
  socket.write(Buffer.concat([header, payload]));
}

server.listen(port, "127.0.0.1", () => log(`mock CDP listening on ${port}`));
