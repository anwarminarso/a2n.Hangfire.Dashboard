// -----------------------------------------------------------------------------
// Tiny vanilla SPA router (History API).
//
// This mimics a real SPA: client-side routing with pushState + a server-side
// catch-all (MapFallbackToFile) so deep links resolve to index.html.
//
// The important integration detail: the router ONLY intercepts anchors marked
// with data-spa. The "Hangfire Dashboard" link is a plain anchor, so clicking it
// performs a full browser navigation to /hangfire, which the server routes to
// the dashboard branch (never this SPA).
// -----------------------------------------------------------------------------

const routes = {
    "/": renderHome,
    "/jobs": renderJobs,
    "/about": renderAbout,
};

const app = document.getElementById("app");

async function renderHome() {
    app.innerHTML = `
        <section class="hero">
            <h1>Home</h1>
            <p>This page is rendered entirely on the client. It fetches live Hangfire
               statistics from the host's minimal API at <code>/api/stats</code>.</p>
        </section>
        <section id="stats" class="cards"><p class="muted">Loading stats…</p></section>
        <section class="hint">
            <p>The <strong>Hangfire Dashboard</strong> link in the top bar opens
               <code>/hangfire</code> as a full-page app. It runs in its own Blazor Server
               circuit and is completely isolated from this SPA.</p>
        </section>`;

    try {
        const res = await fetch("/api/stats");
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const s = await res.json();
        renderStatCards(s);
    } catch (err) {
        document.getElementById("stats").innerHTML =
            `<p class="error">Could not load stats: ${err.message}</p>`;
    }
}

function renderStatCards(s) {
    const cards = [
        ["Enqueued", s.enqueued],
        ["Scheduled", s.scheduled],
        ["Processing", s.processing],
        ["Succeeded", s.succeeded],
        ["Failed", s.failed],
        ["Recurring", s.recurring],
        ["Servers", s.servers],
        ["Queues", s.queues],
    ];
    document.getElementById("stats").innerHTML = cards
        .map(([label, value]) => `
            <div class="card">
                <div class="card-value">${value}</div>
                <div class="card-label">${label}</div>
            </div>`)
        .join("");
}

async function renderJobs() {
    app.innerHTML = `
        <section class="hero">
            <h1>Jobs</h1>
            <p>This route talks to the host's <code>JobsController</code> (an attribute-routed
               API controller). Enqueue a job below — the controller fires a Hangfire job and
               pushes a SignalR notification back to every connected client.</p>
            <p>
                <button id="enqueue-btn" class="btn">Enqueue a job</button>
                <a href="/hangfire" class="btn ghost">Open Hangfire Dashboard &rarr;</a>
            </p>
            <h2>Recent succeeded jobs</h2>
            <ul id="recent-jobs"><li class="muted">Loading…</li></ul>
        </section>`;

    document.getElementById("enqueue-btn").addEventListener("click", async () => {
        try {
            const res = await fetch("/api/jobs/enqueue", { method: "POST" });
            const data = await res.json();
            addNotification("you", `Requested enqueue → ${data.id}`);
            setTimeout(loadRecentJobs, 1500);
        } catch (err) {
            addNotification("error", err.message);
        }
    });

    loadRecentJobs();
}

async function loadRecentJobs() {
    const list = document.getElementById("recent-jobs");
    if (!list) return;
    try {
        const res = await fetch("/api/jobs");
        const jobs = await res.json();
        list.innerHTML = jobs.length
            ? jobs.map((j) => `<li><code>${j.job}</code> <span class="muted">${j.id}</span></li>`).join("")
            : `<li class="muted">No succeeded jobs yet.</li>`;
    } catch (err) {
        list.innerHTML = `<li class="error">${err.message}</li>`;
    }
}

function renderAbout() {
    app.innerHTML = `
        <section class="hero">
            <h1>About</h1>
            <p>SampleAppSpa shows the Hangfire Dashboard UI running next to a Single Page
               Application. The dashboard is mounted with
               <code>app.UseHangfireDashboardUI("/hangfire")</code> <em>before</em> the SPA
               fallback, so both coexist cleanly.</p>
        </section>`;
}

function navigate(path) {
    const render = routes[path] || renderNotFound;
    render();
    setActiveLink(path);
}

function renderNotFound() {
    app.innerHTML = `<section class="hero"><h1>404</h1><p>Unknown SPA route.</p></section>`;
}

function setActiveLink(path) {
    document.querySelectorAll("nav a[data-spa]").forEach((a) => {
        a.classList.toggle("active", new URL(a.href).pathname === path);
    });
}

// Intercept only internal (data-spa) links; let everything else navigate normally.
document.addEventListener("click", (e) => {
    const link = e.target.closest("a[data-spa]");
    if (!link) return;
    e.preventDefault();
    const path = new URL(link.href).pathname;
    history.pushState({}, "", path);
    navigate(path);
});

// Support browser back/forward.
window.addEventListener("popstate", () => navigate(location.pathname));

// Initial render based on the current URL (handles deep links via server fallback).
navigate(location.pathname);

// -----------------------------------------------------------------------------
// Host SignalR connection (/hubs/notifications).
//
// This is the SPA subscribing to the HOST app's own hub — entirely separate from
// the dashboard's Blazor circuit. Both use SignalR and run in the same process
// without conflict because they are mapped at different paths.
// -----------------------------------------------------------------------------
function addNotification(user, message) {
    const list = document.getElementById("notification-list");
    if (!list) return;
    const li = document.createElement("li");
    const time = new Date().toLocaleTimeString();
    li.innerHTML = `<span class="muted">${time}</span> <strong>${user}</strong>: ${message}`;
    list.prepend(li);
    while (list.children.length > 8) list.removeChild(list.lastChild);
}

function setHubStatus(text, cls) {
    const badge = document.getElementById("hub-status");
    if (!badge) return;
    badge.textContent = text;
    badge.className = `badge ${cls || ""}`;
}

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/notifications")
    .withAutomaticReconnect()
    .build();

connection.on("ReceiveMessage", addNotification);
connection.onreconnecting(() => setHubStatus("reconnecting…", "warn"));
connection.onreconnected(() => setHubStatus("connected", "ok"));
connection.onclose(() => setHubStatus("disconnected", "err"));

connection
    .start()
    .then(() => setHubStatus("connected", "ok"))
    .catch((err) => {
        setHubStatus("failed", "err");
        console.error("SignalR connect failed:", err);
    });
