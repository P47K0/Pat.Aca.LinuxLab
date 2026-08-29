import { Hono } from "hono";

type Bindings = {
  LAB_HUB_URL: string;
};

const app = new Hono<{ Bindings: Bindings }>();

app.get("/", (c) => c.html(renderPage(c.env.LAB_HUB_URL)));

app.get("/healthz", (c) => c.json({ status: "ok" }));

export default app;

// One page: a terminal (xterm.js) wired to the API's SignalR hub, plus a
// checklist panel that lights up as the install/upgrade simulator reports
// progress (see the BRD's §04 diagram and §06). Cloudflare Access gates this
// whole hostname at the edge — see the repo README — so there's no login UI
// here, just the identity Access has already established.
function renderPage(hubUrl: string): string {
  return /* html */ `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>CKA Practice Lab</title>
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/xterm@5.3.0/css/xterm.min.css">
<style>
  :root {
    --ink: #e7ecef;
    --ink-soft: #9fadb5;
    --ground: #0d1216;
    --panel: #151b20;
    --line: #29333a;
    --accent: #6fa8d8;
    --ok: #5cca94;
    --pending: #4b5860;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0;
    background: var(--ground);
    color: var(--ink);
    font-family: 'IBM Plex Sans', 'Segoe UI', sans-serif;
    height: 100vh;
    display: flex;
    flex-direction: column;
  }
  header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0.7rem 1.2rem;
    border-bottom: 1px solid var(--line);
    flex-shrink: 0;
  }
  header a { color: var(--ink); text-decoration: none; font-weight: 600; }
  header a:hover { color: var(--accent); }
  #status { font-family: monospace; font-size: 0.8rem; color: var(--ink-soft); }
  main {
    flex: 1;
    display: flex;
    min-height: 0;
  }
  #term-wrap { flex: 1; padding: 0.8rem; min-width: 0; }
  #terminal { height: 100%; }
  aside {
    width: 300px;
    flex-shrink: 0;
    border-left: 1px solid var(--line);
    padding: 1rem;
    overflow-y: auto;
  }
  aside h2 {
    font-size: 0.8rem;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--ink-soft);
    margin: 0 0 0.8rem;
  }
  #checklist { list-style: none; margin: 0; padding: 0; font-size: 0.88rem; }
  #checklist li {
    display: flex;
    align-items: baseline;
    gap: 0.5rem;
    padding: 0.35rem 0;
    color: var(--ink-soft);
  }
  #checklist li.ok { color: var(--ink); }
  #checklist li .dot {
    width: 0.5rem; height: 0.5rem; border-radius: 50%;
    background: var(--pending); flex-shrink: 0;
  }
  #checklist li.ok .dot { background: var(--ok); }
  #checklist .empty { color: var(--ink-soft); font-size: 0.85rem; }
</style>
</head>
<body>
  <header>
    <a href="https://www.koorevaar.com">koorevaar.com</a>
    <span>CKA Practice Lab</span>
    <span id="status">connecting…</span>
  </header>
  <main>
    <div id="term-wrap"><div id="terminal"></div></div>
    <aside>
      <h2>Progress</h2>
      <ul id="checklist"><li class="empty">Nothing run yet.</li></ul>
    </aside>
  </main>

  <script src="https://cdn.jsdelivr.net/npm/xterm@5.3.0/lib/xterm.js"></script>
  <script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.0/dist/browser/signalr.min.js"></script>
  <script>
    const term = new Terminal({
      convertEol: true,
      fontFamily: "'IBM Plex Mono', monospace",
      fontSize: 14,
      theme: { background: "#0d1216", foreground: "#e7ecef" },
    });
    term.open(document.getElementById("terminal"));

    const statusEl = document.getElementById("status");
    const checklistEl = document.getElementById("checklist");
    const seenSteps = new Set();

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(${JSON.stringify(hubUrl)}, { withCredentials: true })
      .withAutomaticReconnect()
      .build();

    connection.on("ReceiveOutput", (chunk) => term.write(chunk));

    connection.on("ChecklistUpdate", (evt) => {
      if (checklistEl.querySelector(".empty")) checklistEl.innerHTML = "";
      let li = document.getElementById("step-" + evt.step);
      if (!li) {
        li = document.createElement("li");
        li.id = "step-" + evt.step;
        li.innerHTML = '<span class="dot"></span><span class="label"></span>';
        checklistEl.appendChild(li);
      }
      li.classList.toggle("ok", evt.status === "ok");
      li.querySelector(".label").textContent = evt.message || evt.step;
    });

    term.onData((data) => {
      if (connection.state === signalR.HubConnectionState.Connected) {
        connection.invoke("SendInput", data).catch(console.error);
      }
    });

    connection
      .start()
      .then(() => { statusEl.textContent = "connected"; term.focus(); })
      .catch((err) => {
        statusEl.textContent = "disconnected";
        term.writeln("\\r\\n[lab] could not connect to the session API: " + err);
      });

    connection.onreconnecting(() => (statusEl.textContent = "reconnecting…"));
    connection.onreconnected(() => (statusEl.textContent = "connected"));
    connection.onclose(() => (statusEl.textContent = "disconnected"));
  </script>
</body>
</html>`;
}
