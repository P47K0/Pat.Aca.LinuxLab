import { Hono } from "hono";

type Bindings = {
  LAB_HUB_URL: string;
  // Service binding to koorevaar.com's own Worker — a direct in-process
  // call, not a real network fetch, so no public URL/CORS/auth needed.
  // Its fetch() handler runs handleContactForm() (or whatever routes to
  // it) and returns { success, message } — that's koorevaar.com's own
  // response shape, confirmed from its actual source, not guessed.
  CONTACT_WORKER: Fetcher;
};

const app = new Hono<{ Bindings: Bindings }>();

app.get("/", (c) => c.html(renderPage(c.env.LAB_HUB_URL)));

app.get("/healthz", (c) => c.json({ status: "ok" }));

// Server-side proxy for the feedback form — the browser posts here
// (same-origin, no CORS to configure anywhere), and this forwards to
// koorevaar.com's own Worker via the CONTACT_WORKER service binding
// instead of the browser calling it directly.
app.post("/feedback", async (c) => {
  const body = await c.req.json().catch(() => null);
  const name = typeof body?.name === "string" ? body.name.trim() : "";
  const email = typeof body?.email === "string" ? body.email.trim() : "";
  const message = typeof body?.message === "string" ? body.message.trim() : "";

  if (!name || !email || !message) {
    return c.json({ success: false, message: "Name, email, and message are all required." }, 400);
  }

  // The host here doesn't need to be real — a service binding routes
  // straight to the target Worker's own fetch() handler, not over the
  // network — but the path does need to match what that Worker actually
  // dispatches on. "/api/contact" confirmed directly from koorevaar.com's
  // own contact page JS, not guessed.
  const upstream = await c.env.CONTACT_WORKER.fetch("https://internal/api/contact", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(buildContactPayload(name, email, message)),
  });

  const result = await upstream.json().catch(() => ({ success: false, message: "Unexpected response from the contact worker." }));
  return c.json(result, upstream.ok ? 200 : 502);
});

export default app;

function buildContactPayload(name: string, email: string, message: string) {
  return { name, email, subject: "CKA Lab feedback", message };
}

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
  /* Shared look for the header's two small action controls (Feedback,
     Buy me a coffee) — one a <button>, one an <a>, same appearance. */
  .header-btn {
    flex-shrink: 0;
    display: inline-flex;
    align-items: center;
    gap: 0.3rem;
    background: none;
    border: 1px solid var(--line);
    color: var(--ink-soft);
    text-decoration: none;
    border-radius: 4px;
    padding: 0.3rem 0.6rem;
    font-family: inherit;
    font-size: 0.8rem;
    cursor: pointer;
  }
  .header-btn:hover { color: var(--ink); border-color: var(--accent); }

  /* Popup, not a new page — stays on top of the terminal, never navigates
     away from the running session (the whole point of the request). */
  .modal-overlay {
    display: none;
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.5);
    align-items: center;
    justify-content: center;
    padding: 1rem;
    z-index: 10;
  }
  .modal-overlay.open { display: flex; }
  .modal {
    background: var(--panel);
    border: 1px solid var(--line);
    border-radius: 8px;
    padding: 1.2rem;
    width: 100%;
    max-width: 26rem;
  }
  .modal h2 { margin: 0 0 0.9rem; font-size: 1rem; }
  .modal label { display: block; font-size: 0.82rem; color: var(--ink-soft); margin: 0.7rem 0 0.3rem; }
  .modal input, .modal textarea {
    width: 100%;
    background: var(--ground);
    border: 1px solid var(--line);
    color: var(--ink);
    border-radius: 4px;
    padding: 0.5rem 0.6rem;
    font-family: inherit;
    font-size: 0.9rem;
  }
  .modal textarea { resize: vertical; min-height: 5rem; }
  .modal input:focus, .modal textarea:focus { outline: 2px solid var(--accent); outline-offset: 1px; }
  .modal-actions { display: flex; justify-content: flex-end; gap: 0.6rem; margin-top: 1rem; }
  .modal-actions button {
    font-family: inherit;
    font-size: 0.85rem;
    padding: 0.45rem 0.9rem;
    border-radius: 4px;
    cursor: pointer;
  }
  #feedback-cancel { background: none; border: 1px solid var(--line); color: var(--ink-soft); }
  #feedback-cancel:hover { color: var(--ink); }
  #feedback-submit { background: var(--accent); border: 1px solid var(--accent); color: var(--ground); font-weight: 600; }
  #feedback-submit:disabled { opacity: 0.6; cursor: default; }
  #feedback-status { font-size: 0.82rem; margin-top: 0.6rem; min-height: 1.1em; }
  #feedback-status.error { color: #e08a8a; }
  #feedback-status.ok { color: var(--ok); }
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

  /* Deliberately thin — this page's vertical space is already tight on
     mobile (see the breakpoint below), so this shouldn't cost the
     terminal any more of it than it has to. */
  footer {
    flex-shrink: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    padding: 0.35rem 1rem;
    border-top: 1px solid var(--line);
    font-size: 0.75rem;
    color: var(--ink-soft);
  }
  footer a { color: var(--ink-soft); text-decoration: none; }
  footer a:hover { color: var(--accent); }
  footer .sep { opacity: 0.5; }

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
    header { padding: 0.6rem 0.8rem; gap: 0.4rem; }
    header span:not(.brand) { font-size: 0.75rem; }
    .header-btn { font-size: 0.75rem; padding: 0.25rem 0.5rem; }
    /* Text labels are the first thing to go on the coffee link on a
       narrow screen — the emoji alone is enough, and the header is
       already juggling four items by this point. */
    #coffee-link .btn-label { display: none; }
  }
</style>
</head>
<body>
  <header>
    <a href="https://www.koorevaar.com">koorevaar.com</a>
    <span class="brand">CKA Practice Lab</span>
    <button id="feedback-open" class="header-btn" type="button">Feedback</button>
    <a id="coffee-link" class="header-btn" href="https://paypal.me/p47k0" target="_blank" rel="noopener">☕ <span class="btn-label">Buy me a coffee</span></a>
    <span id="status">connecting…</span>
  </header>
  <main>
    <div id="term-wrap"><div id="terminal"></div></div>
    <aside>
      <h2>Progress</h2>
      <ul id="checklist"><li class="empty">Nothing run yet.</li></ul>
    </aside>
  </main>
  <footer>
    <a href="https://github.com/P47K0/Pat.Aca.LinuxLab" target="_blank" rel="noopener">This lab's source</a>
    <span class="sep">·</span>
    <a href="https://github.com/P47K0/k8s-whizlabs-sandbox" target="_blank" rel="noopener">2-node sandbox scripts</a>
  </footer>

  <div class="modal-overlay" id="feedback-overlay">
    <form class="modal" id="feedback-form">
      <h2>Send feedback</h2>
      <label for="feedback-name">Name</label>
      <input id="feedback-name" name="name" type="text" required>
      <label for="feedback-email">Email</label>
      <input id="feedback-email" name="email" type="email" required>
      <label for="feedback-message">Message</label>
      <textarea id="feedback-message" name="message" required></textarea>
      <div id="feedback-status" role="status"></div>
      <div class="modal-actions">
        <button id="feedback-cancel" type="button">Cancel</button>
        <button id="feedback-submit" type="submit">Send</button>
      </div>
    </form>
  </div>

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

    // Feedback popup — stays on this same page/tab and this same running
    // session the whole time; the terminal and its SignalR connection are
    // untouched by any of this.
    const feedbackOverlay = document.getElementById("feedback-overlay");
    const feedbackForm = document.getElementById("feedback-form");
    const feedbackStatus = document.getElementById("feedback-status");
    const feedbackSubmit = document.getElementById("feedback-submit");

    const openFeedback = () => {
      feedbackStatus.textContent = "";
      feedbackStatus.className = "";
      feedbackOverlay.classList.add("open");
      document.getElementById("feedback-name").focus();
    };
    const closeFeedback = () => feedbackOverlay.classList.remove("open");

    document.getElementById("feedback-open").addEventListener("click", openFeedback);
    document.getElementById("feedback-cancel").addEventListener("click", closeFeedback);
    feedbackOverlay.addEventListener("click", (e) => { if (e.target === feedbackOverlay) closeFeedback(); });
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && feedbackOverlay.classList.contains("open")) closeFeedback();
    });

    feedbackForm.addEventListener("submit", async (e) => {
      e.preventDefault();
      feedbackSubmit.disabled = true;
      feedbackStatus.className = "";
      feedbackStatus.textContent = "Sending…";

      try {
        const res = await fetch("/feedback", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            name: document.getElementById("feedback-name").value,
            email: document.getElementById("feedback-email").value,
            message: document.getElementById("feedback-message").value,
          }),
        });
        const result = await res.json();
        if (!res.ok || !result.success) throw new Error(result.message || "Something went wrong.");

        feedbackStatus.className = "ok";
        feedbackStatus.textContent = "Thanks — sent!";
        setTimeout(() => { closeFeedback(); feedbackForm.reset(); }, 1200);
      } catch (err) {
        feedbackStatus.className = "error";
        feedbackStatus.textContent = err.message || "Something went wrong. Please try again.";
      } finally {
        feedbackSubmit.disabled = false;
      }
    });
  </script>
</body>
</html>`;
}
