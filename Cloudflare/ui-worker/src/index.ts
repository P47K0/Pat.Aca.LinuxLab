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
  html, body { overflow: hidden; } /* the terminal and aside scroll internally — the page itself never should */
  body {
    margin: 0;
    background: var(--ground);
    color: var(--ink);
    font-family: 'IBM Plex Sans', 'Segoe UI', sans-serif;
    /* 100vh doesn't track mobile browsers' address bar show/hide, so the
       page can end up taller than what's actually visible — 100dvh does.
       Kept as two declarations, not one: browsers that don't understand
       dvh discard that whole line and silently keep the 100vh above,
       rather than erroring. */
    height: 100vh;
    height: 100dvh;
    display: flex;
    flex-direction: column;
  }
  header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.6rem;
    padding: 0.7rem 1.2rem;
    border-bottom: 1px solid var(--line);
    flex-shrink: 0;
  }
  header a { color: var(--ink); text-decoration: none; font-weight: 600; flex-shrink: 0; }
  header a:hover { color: var(--accent); }
  /* The middle "CKA Practice Lab" label is the first to go on a narrow
     screen — it's already the page <title>, purely decorative here, and
     was the piece most likely to get squeezed onto its own cramped line
     next to the other two. */
  header .brand { flex-shrink: 999; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  #status { font-family: monospace; font-size: 0.8rem; color: var(--ink-soft); flex-shrink: 0; }
  main {
    flex: 1;
    display: flex;
    min-height: 0;
  }
  #term-wrap { flex: 1; padding: 0.8rem; min-width: 0; min-height: 0; }
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

  /* Below this, there isn't room for the terminal and the 300px sidebar
     side by side without squeezing the terminal to near-uselessness (the
     original bug report) — stack instead, terminal first since it's what
     you're actually there to use, checklist below in its own scrollable
     strip rather than pushing the terminal off-screen. */
  @media (max-width: 760px) {
    main { flex-direction: column; }
    #term-wrap { flex: 1 1 auto; min-height: 45%; padding: 0.6rem; }
    aside {
      width: auto;
      flex: 0 0 auto;
      max-height: 35vh;
      border-left: none;
      border-top: 1px solid var(--line);
    }
    header { padding: 0.6rem 0.8rem; }
    header span:not(.brand) { font-size: 0.75rem; }
  }
</style>
</head>
<body>
  <header>
    <a href="https://www.koorevaar.com">koorevaar.com</a>
    <span class="brand">CKA Practice Lab</span>
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
  <script src="https://cdn.jsdelivr.net/npm/xterm-addon-fit@0.8.0/lib/xterm-addon-fit.js"></script>
  <script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.0/dist/browser/signalr.min.js"></script>
  <script>
    const term = new Terminal({
      convertEol: true,
      fontFamily: "'IBM Plex Mono', monospace",
      fontSize: 14,
      theme: { background: "#0d1216", foreground: "#e7ecef" },
    });
    // Without this, the terminal keeps whatever cols/rows it had when it
    // was first opened — on a phone, the visible box is far narrower than
    // that, so the remote shell (which never hears about the mismatch
    // either, see onResize below) wraps and redraws lines assuming a width
    // that isn't real, which is what actually produces "text on top of
    // other text": a real terminal-size mismatch, not a CSS bug.
    const fitAddon = new FitAddon.FitAddon();
    term.loadAddon(fitAddon);
    term.open(document.getElementById("terminal"));
    fitAddon.fit();

    // Re-fit on any real size change to the terminal's own container —
    // not just window resize, since that alone misses some of the cases
    // that matter most on mobile (address bar show/hide, the on-screen
    // keyboard opening, the checklist panel's layout flipping at the
    // 760px breakpoint above) without necessarily firing a window resize
    // event at all.
    let fitScheduled = false;
    const scheduleFit = () => {
      if (fitScheduled) return;
      fitScheduled = true;
      requestAnimationFrame(() => {
        fitScheduled = false;
        fitAddon.fit();
      });
    };
    new ResizeObserver(scheduleFit).observe(document.getElementById("term-wrap"));

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

    connection.on("SessionEnded", (message) => {
      statusEl.textContent = "session ended";
      term.writeln("\\r\\n[lab] " + message);
    });

    connection.on("SessionRejected", (message) => {
      statusEl.textContent = "rejected";
      term.writeln("\\r\\n[lab] " + message);
    });

    term.onData((data) => {
      if (connection.state === signalR.HubConnectionState.Connected) {
        connection.invoke("SendInput", data).catch(console.error);
      }
    });

    // Tells the container's real PTY the terminal's real size (see the
    // fitAddon comment above) — fires on every fit(), including the very
    // first one at page load, which happens before the connection exists,
    // so it's re-sent explicitly on (re)connect too rather than relying on
    // that first onResize alone.
    const sendResize = () => {
      if (connection.state === signalR.HubConnectionState.Connected) {
        connection.invoke("ResizeTerminal", term.cols, term.rows).catch(console.error);
      }
    };
    term.onResize(sendResize);

    connection
      .start()
      .then(() => { statusEl.textContent = "connected"; term.focus(); sendResize(); })
      .catch((err) => {
        statusEl.textContent = "disconnected";
        term.writeln("\\r\\n[lab] could not connect to the session API: " + err);
      });

    connection.onreconnecting(() => (statusEl.textContent = "reconnecting…"));
    connection.onreconnected(() => { statusEl.textContent = "connected"; sendResize(); });
    connection.onclose(() => (statusEl.textContent = "disconnected"));
  </script>
</body>
</html>`;
}
