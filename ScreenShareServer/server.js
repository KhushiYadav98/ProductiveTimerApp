// Serves the sender page (http://localhost:8080) and relays WebRTC signaling
// between one sender (PC browser) and one receiver (Quest app) over WebSocket.
//
// Messages are JSON: { type: "hello", role: "sender" | "receiver" }, then
// "offer" / "answer" / "candidate" which are forwarded as-is to the other peer.

const dgram = require("dgram");
const http = require("http");
const fs = require("fs");
const os = require("os");
const path = require("path");
const { WebSocketServer } = require("ws");

const PORT = Number(process.env.PORT) || 8080;
const DISCOVERY_PORT = 47800;
const PAGE = path.join(__dirname, "public", "index.html");

const server = http.createServer((req, res) => {
  if (req.url === "/" || req.url === "/index.html") {
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    fs.createReadStream(PAGE).pipe(res);
  } else {
    res.writeHead(404).end();
  }
});

const wss = new WebSocketServer({ server });
const peers = { sender: null, receiver: null };

function other(role) {
  return role === "sender" ? peers.receiver : peers.sender;
}

function send(ws, msg) {
  if (ws && ws.readyState === ws.OPEN) ws.send(JSON.stringify(msg));
}

wss.on("connection", (ws, req) => {
  let role = null;

  ws.on("message", (data) => {
    let msg;
    try { msg = JSON.parse(data); } catch { return; }

    if (msg.type === "hello" && (msg.role === "sender" || msg.role === "receiver")) {
      role = msg.role;
      if (peers[role] && peers[role] !== ws) peers[role].close();
      peers[role] = ws;
      console.log(`${role} connected from ${req.socket.remoteAddress}`);

      // Tell each side whether the other is present so the sender knows when to (re)offer.
      send(other(role), { type: "peer-joined", role });
      if (other(role)) send(ws, { type: "peer-joined", role: role === "sender" ? "receiver" : "sender" });
      return;
    }

    if (role) send(other(role), msg);
  });

  ws.on("close", () => {
    if (role && peers[role] === ws) {
      peers[role] = null;
      console.log(`${role} disconnected`);
      send(other(role), { type: "peer-left", role });
    }
  });
});

// LAN discovery: the Quest app broadcasts "PT_DISCOVER" and we reply directly with our signaling port,
// so the app finds this PC even when its IP address changes.
const discovery = dgram.createSocket({ type: "udp4", reuseAddr: true });
discovery.on("message", (msg, rinfo) => {
  if (msg.toString() !== "PT_DISCOVER") return;
  discovery.send(`PT_SERVER:${PORT}`, rinfo.port, rinfo.address);
  console.log(`discovery request from ${rinfo.address}`);
});
discovery.on("error", (e) => console.warn(`discovery disabled: ${e.message}`));
discovery.bind(DISCOVERY_PORT);

server.listen(PORT, () => {
  const ips = Object.values(os.networkInterfaces()).flat()
    .filter((i) => i && i.family === "IPv4" && !i.internal)
    .map((i) => i.address);
  console.log(`Sender page:   http://localhost:${PORT}`);
  console.log(`Quest app URL: ${ips.map((ip) => `ws://${ip}:${PORT}`).join("  or  ")}`);
});
