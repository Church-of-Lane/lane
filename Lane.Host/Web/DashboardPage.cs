namespace Lane.Host.Web;

/// <summary>The dashboard's single page. Self-contained so the server needs no static files.</summary>
public static class DashboardPage
{
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>lane</title>
        <style>
          :root { color-scheme: dark; }
          * { box-sizing: border-box; }
          body {
            margin: 0; background: #0d0f12; color: #d7dbe0;
            font: 13px/1.4 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
          }
          header {
            padding: 10px 16px; border-bottom: 1px solid #23272e;
            display: flex; align-items: center; gap: 12px;
          }
          header h1 { font-size: 14px; margin: 0; letter-spacing: 0.08em; text-transform: uppercase; color: #8fb4ff; }
          header .dot { width: 8px; height: 8px; border-radius: 50%; background: #4a5058; }
          header .dot.live { background: #59d18a; }
          header a {
            margin-left: auto; color: #8fb4ff; text-decoration: none; font-size: 12px;
            border: 1px solid #2c313a; border-radius: 5px; padding: 5px 10px;
          }
          header a:hover { background: #1c2027; }
          main { display: grid; grid-template-columns: 1fr 1fr; grid-template-rows: auto auto auto auto 1fr; gap: 12px; padding: 12px; height: calc(100vh - 45px); }
          .panel { background: #14171c; border: 1px solid #23272e; border-radius: 6px; padding: 10px 12px; overflow: auto; }
          .panel h2 { font-size: 11px; text-transform: uppercase; letter-spacing: 0.08em; color: #8892a0; margin: 0 0 8px; }
          .span2 { grid-column: 1 / span 2; }
          table { width: 100%; border-collapse: collapse; font-size: 12px; }
          th, td { text-align: left; padding: 3px 8px 3px 0; white-space: nowrap; }
          th { color: #6b7280; font-weight: 600; }
          tr:nth-child(even) td { background: rgba(255,255,255,0.02); }
          .bar { height: 10px; border-radius: 5px; background: #23272e; overflow: hidden; margin: 6px 0; }
          .bar > div { height: 100%; background: linear-gradient(90deg, #59d18a, #8fb4ff); }
          .muted { color: #6b7280; }
          #log { font-size: 11.5px; white-space: pre-wrap; word-break: break-all; }
          #log div { padding: 1px 0; border-bottom: 1px dotted rgba(255,255,255,0.03); }
          .logsPanel { grid-row: -2 / -1; }
          #sessions tr { cursor: pointer; }
          #sessions tr:hover td { background: #1c2027; }
          .warn { color: #f0b35a; }
          #drawer {
            position: fixed; top: 0; right: 0; width: min(560px, 100vw); height: 100vh; display: none;
            flex-direction: column; background: #111419; border-left: 1px solid #2c313a; z-index: 10;
          }
          #drawer.open { display: flex; }
          #drawer header { border-bottom: 1px solid #23272e; }
          #drawer header button, #sendForm button {
            background: #1c2027; color: #d7dbe0; border: 1px solid #2c313a; border-radius: 5px;
            padding: 5px 10px; font: inherit; cursor: pointer;
          }
          #drawerStatus { padding: 6px 16px; border-bottom: 1px solid #23272e; }
          #messages { flex: 1; overflow: auto; padding: 10px 16px; }
          .msg { margin: 0 0 10px; }
          .msg .who { color: #8892a0; font-size: 11px; }
          .msg.lane .who { color: #8fb4ff; }
          .msg .text { white-space: pre-wrap; word-break: break-word; }
          .msg.Observation .text { color: #8892a0; }
          #sendForm { display: flex; gap: 8px; padding: 10px 16px; border-top: 1px solid #23272e; }
          #sendText {
            flex: 1; min-height: 38px; max-height: 160px; resize: vertical; background: #0d0f12; color: #d7dbe0;
            border: 1px solid #2c313a; border-radius: 5px; padding: 6px 8px; font: inherit;
          }
        </style>
        </head>
        <body>
        <header>
          <h1>lane</h1>
          <span class="dot" id="liveDot"></span>
          <span class="muted" id="asOf"></span>
          <a href="/config" target="_blank" rel="noopener">Config ↗</a>
        </header>
        <main>
          <section class="panel">
            <h2>Energy</h2>
            <div class="bar"><div id="energyBar" style="width:0%"></div></div>
            <div id="energyText" class="muted"></div>
          </section>
          <section class="panel">
            <h2>Inner Life</h2>
            <div id="monoNext"></div>
            <div id="monoThought" class="muted" style="margin-top:6px"></div>
          </section>
          <section class="panel span2">
            <h2>Sessions</h2>
            <table>
              <thead><tr><th>session</th><th>name</th><th>state</th><th>activity</th><th>idle</th></tr></thead>
              <tbody id="sessions"></tbody>
            </table>
          </section>
          <section class="panel span2" id="codingPanel" style="display:none">
            <h2>Coding</h2>
            <table>
              <thead><tr><th>project</th><th>claude code</th><th>cost</th><th>waiting on lane</th></tr></thead>
              <tbody id="coding"></tbody>
            </table>
          </section>
          <section class="panel span2">
            <h2>Models</h2>
            <table>
              <thead><tr><th>model</th><th>roles</th><th>calls</th><th>in</th><th>out</th><th>cached</th><th>hit</th><th>last</th></tr></thead>
              <tbody id="models"></tbody>
            </table>
          </section>
          <section class="panel span2 logsPanel">
            <h2>Log</h2>
            <div id="log"></div>
          </section>
        </main>
        <aside id="drawer">
          <header>
            <h1 id="drawerTitle"></h1>
            <button type="button" id="drawerClose" style="margin-left:auto">Close</button>
          </header>
          <div id="drawerStatus" class="muted"></div>
          <div id="messages"></div>
          <form id="sendForm">
            <textarea id="sendText" placeholder="Say something here as yourself"></textarea>
            <button type="submit">Send</button>
          </form>
        </aside>
        <script src="/auth.js"></script>
        <script>
          const el = id => document.getElementById(id);
          const esc = t => String(t ?? "").replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;").replace(/"/g,"&quot;");

          let openId = null;
          let lastCoding = [];

          function idle(iso) {
            const s = (Date.now() - new Date(iso).getTime()) / 1000;
            if (s < 60) return Math.floor(s) + "s";
            if (s < 3600) return Math.floor(s / 60) + "m";
            return Math.floor(s / 3600) + "h";
          }

          function countdown(iso) {
            if (!iso) return "—";
            const s = (new Date(iso).getTime() - Date.now()) / 1000;
            if (s <= 0) return "any moment";
            if (s < 60) return Math.floor(s) + "s";
            if (s < 3600) return Math.floor(s / 60) + "m " + Math.floor(s % 60) + "s";
            return Math.floor(s / 3600) + "h " + Math.floor((s % 3600) / 60) + "m";
          }

          function compact(n) {
            const abs = Math.abs(n);
            if (abs >= 1e6) return (n / 1e6).toFixed(1) + "M";
            if (abs >= 1e3) return (n / 1e3).toFixed(1) + "k";
            return n.toLocaleString();
          }

          function render(snap) {
            el("asOf").textContent = new Date().toLocaleTimeString();

            el("energyBar").style.width = Math.round(snap.energy.fraction * 100) + "%";
            el("energyText").textContent = snap.energy.asleep
              ? `asleep — rested in ${countdown(new Date(Date.now() + snap.energy.restedInSeconds * 1000).toISOString())}`
              : `${compact(snap.energy.remaining)} / ${compact(snap.energy.budget)} tokens — ${snap.energy.tier.toLowerCase()}`;

            el("monoNext").textContent = "next thought: " + countdown(snap.monologue.nextThoughtAt) +
              (snap.monologue.thinking ? "  (thinking)" : "");
            el("monoThought").textContent = snap.monologue.lastThought || "(nothing yet)";

            el("sessions").innerHTML = snap.sessions.length
              ? snap.sessions.map(s => `<tr data-id="${esc(s.id)}" data-name="${esc(s.name)}"><td>${esc(s.id)}</td><td>${esc(s.name)}</td><td>${esc(s.state)}</td><td>${esc(s.activity)}</td><td>${idle(s.lastActivity)}</td></tr>`).join("")
              : `<tr><td class="muted" colspan="5">(no sessions)</td></tr>`;

            lastCoding = snap.coding || [];
            el("codingPanel").style.display = lastCoding.length ? "" : "none";
            el("coding").innerHTML = lastCoding.map(c =>
              `<tr><td>${esc(c.name)}${c.selfHosted ? " (self)" : ""}</td>` +
              `<td>${esc(c.claude)}${c.paused ? ' <span class="warn">paused</span>' : ""}</td>` +
              `<td>$${c.costUsd.toFixed(2)}</td>` +
              `<td class="${c.pendingPermissions.length ? "warn" : "muted"}">${c.pendingPermissions.length ? esc(c.pendingPermissions.join(", ")) : "-"}</td></tr>`).join("");
            renderStatus();

            el("models").innerHTML = snap.models.length
              ? snap.models.map(m => `<tr><td>${m.instance}</td><td>${m.roles}</td><td>${m.calls}</td><td>${m.input}</td><td>${m.output}</td><td>${m.cacheRead}</td><td>${m.cacheRate}</td><td>${m.lastLatencyMs ? m.lastLatencyMs + "ms" : "-"}</td></tr>`).join("")
              : `<tr><td class="muted" colspan="8">(none)</td></tr>`;

            const log = el("log");
            const atBottom = log.scrollTop + log.clientHeight >= log.scrollHeight - 4;
            log.innerHTML = snap.logs.map(l => `<div>${l.replace(/&/g,"&amp;").replace(/</g,"&lt;")}</div>`).join("");
            if (atBottom) log.scrollTop = log.scrollHeight;
          }

          async function poll() {
            try {
              const res = await laneFetch("/api/snapshot");
              render(await res.json());
              el("liveDot").classList.add("live");
            } catch (e) {
              el("liveDot").classList.remove("live");
            } finally {
              setTimeout(poll, 1500);
            }
          }

          function renderStatus() {
            const c = lastCoding.find(c => c.session === openId);
            el("drawerStatus").innerHTML = c
              ? `Claude Code ${esc(c.claude)} · $${c.costUsd.toFixed(2)} · ${esc(c.path)}` +
                (c.paused ? ' · <span class="warn">paused until someone speaks</span>' : "") +
                (c.pendingPermissions.length ? ` · <span class="warn">permission: ${esc(c.pendingPermissions.join(", "))}</span>` : "")
              : "";
            el("drawerStatus").style.display = c ? "" : "none";
          }

          async function loadMessages() {
            if (!openId) return;
            const id = openId;
            try {
              const res = await laneFetch("/api/session/messages?limit=200&id=" + encodeURIComponent(id));
              if (!res.ok || id !== openId) return;
              const rows = await res.json();
              const box = el("messages");
              const atBottom = box.scrollTop + box.clientHeight >= box.scrollHeight - 4;
              box.innerHTML = rows.length
                ? rows.map(m => `<div class="msg ${m.isLane ? "lane" : ""} ${esc(m.kind)}"><div class="who">${esc(m.author)} · ${new Date(m.timestamp).toLocaleTimeString()}</div><div class="text">${esc(m.text)}</div></div>`).join("")
                : '<div class="muted">(nothing said yet)</div>';
              if (atBottom) box.scrollTop = box.scrollHeight;
            } catch (e) { /* the next poll will try again */ }
          }

          function openSession(id, name) {
            openId = id;
            el("drawerTitle").textContent = name || id;
            el("messages").innerHTML = "";
            el("drawer").classList.add("open");
            renderStatus();
            loadMessages();
          }

          el("sessions").addEventListener("click", e => {
            const row = e.target.closest("tr[data-id]");
            if (row) openSession(row.dataset.id, row.dataset.name);
          });

          el("drawerClose").addEventListener("click", () => { openId = null; el("drawer").classList.remove("open"); });

          el("sendForm").addEventListener("submit", async e => {
            e.preventDefault();
            const text = el("sendText").value.trim();
            if (!text || !openId) return;
            const res = await laneFetch("/api/session/messages?id=" + encodeURIComponent(openId), {
              method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ text })
            });
            if (res.ok) { el("sendText").value = ""; setTimeout(loadMessages, 300); }
          });

          el("sendText").addEventListener("keydown", e => {
            if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); el("sendForm").requestSubmit(); }
          });

          setInterval(loadMessages, 2000);
          poll();
        </script>
        </body>
        </html>
        """;
}
