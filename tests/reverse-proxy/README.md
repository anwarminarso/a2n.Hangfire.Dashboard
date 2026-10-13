# Reverse-proxy test environment

A local Docker stack for testing the dashboard the way it often runs in production: behind a TLS-terminating reverse proxy, mounted under a path prefix, with cookie authentication. It was built to reproduce and verify [#61](https://github.com/anwarminarso/a2n.Hangfire.Dashboard/issues/61).

This is a test environment, not a deployment template. It uses hardcoded demo users, a self-signed CA and throwaway database credentials. It isn't part of the solution or CI.

```
browser ─▶ nginx :80/:443 ─▶ app :8080 (.NET 8, Linux) ─▶ PostgreSQL 16
           HTTP→HTTPS        dashboard at /jobs
           TLS termination   cookie auth + IsInRole filter
```

- **nginx** redirects HTTP to HTTPS and terminates TLS, like Nginx Proxy Manager. Its access log uses the same fields as the issue report, plus timings.
- **app** builds the dashboard from the local `src/` with a PostgreSQL adapter on its own connection string. On first start it seeds about 8,000 jobs and a few recurring jobs, then enqueues a short job every 3 seconds.
- The app resolves the public host name to the Docker host, so a request the server makes to its own public URL goes back out through nginx, as with NAT hairpinning in a real deployment.

## Usage

From this folder:

```powershell
docker compose up -d --build        # build from src/ and start (rerun after code changes)
# open https://hangfire.127.0.0.1.nip.io/  (admin/admin has the role, viewer/viewer doesn't)

powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1         # log in, load one page, summarize hub requests
powershell -ExecutionPolicy Bypass -File scripts\hub-summary.ps1   # summarize hub requests in the nginx log (-Clear to reset)
docker compose logs -f app                                         # HubRequests lines = hub calls that reached the app

docker compose down        # stop, keep the database
docker compose down -v     # stop and drop the database
```

Ports: 80/443 (nginx, all interfaces), 127.0.0.1:5180 (app, bypassing nginx), 127.0.0.1:55432 (PostgreSQL).

To avoid browser certificate warnings, trust the generated CA for the current user, and remove it when you're done, since its private key sits in `.generated/certs`:

```powershell
certutil -user -addstore Root .generated\certs\ca.crt
certutil -user -delstore Root "Hangfire reverse-proxy test CA"
```

Settings (environment variables or a `.env` file next to `compose.yaml`) are listed at the top of [`compose.yaml`](compose.yaml): target framework, forwarded headers, login path, `StatsPollingInterval`, `EnablePrerendering` and `DemandRollupEnabled`.
