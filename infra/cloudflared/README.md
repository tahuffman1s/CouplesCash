# Cloudflare Tunnel for CouplesCash

The tunnel is how your phones reach the Pi from anywhere **without opening a
single port on your router**. `cloudflared` runs as a container in the same
compose network as `web`, makes an *outbound* connection to Cloudflare, and
Cloudflare forwards `https://cash.yourdomain.com` down that connection to
`http://web:3000`. The API and Postgres are never reachable from outside.

Prerequisites: a domain whose DNS is managed by Cloudflare (the free plan is
fine) and the stack installed on the Pi (`docs/RASPBERRY-PI.md`).

## 1. Create the tunnel (≈ 5 minutes, all in the browser)

1. Open <https://one.dash.cloudflare.com> → pick your account → **Networks → Tunnels**.
2. **Create a tunnel** → connector type **Cloudflared** → name it `couplescash` → **Save tunnel**.
3. On the *Install and run a connector* page pick **Docker**. You do **not** need to run the command shown — just copy the long string after `--token`. That is your `TUNNEL_TOKEN` (it starts with `eyJ`).
4. On the Pi, put it in `.env`:

   ```ini
   TUNNEL_TOKEN=eyJhIjoi…
   PUBLIC_URL=https://cash.yourdomain.com
   ```

5. Click **Next** to reach *Route tunnel* → **Public Hostname**:

   | Field | Value |
   |---|---|
   | Subdomain | `cash` |
   | Domain | `yourdomain.com` |
   | Path | *(empty)* |
   | Type | `HTTP` |
   | URL | `web:3000` |

   `web` is the compose **service name**; cloudflared resolves it through
   Docker's internal DNS because both containers share the `couplescash`
   network. Nothing needs to be published on the Pi's LAN interface.

6. **Save hostname**. Cloudflare creates the `CNAME cash → <tunnel-id>.cfargotunnel.com` DNS record for you.

## 2. Start it

```bash
make tunnel-up      # = docker compose --profile tunnel up -d
make ps             # cloudflared should be "Up", web "healthy"
make logs S=cloudflared
```

A healthy connector logs `Registered tunnel connection` (usually four of them,
one per Cloudflare edge). The dashboard shows the tunnel as **Healthy**.

Open `https://cash.yourdomain.com` on your phone → **Register** → Settings →
**Install app**.

To start the tunnel automatically with plain `make up`, set
`COMPOSE_PROFILES=tunnel` in `.env`.

## 3. `ORIGIN` must match exactly — or forms 403

SvelteKit's CSRF protection compares each form POST's `Origin` header with the
`ORIGIN` environment variable of the web container (`PUBLIC_URL` in `.env`).
They must be **identical**: same scheme, same host, no trailing slash, no path.

| `PUBLIC_URL` | Result |
|---|---|
| `https://cash.yourdomain.com` | ✅ |
| `https://cash.yourdomain.com/` | ❌ 403 on login |
| `http://cash.yourdomain.com` | ❌ (tunnel serves https) |
| `https://www.cash.yourdomain.com` | ❌ unless that is the hostname you route |

After changing `PUBLIC_URL`: `make up` (recreates `web`).

## 4. Recommended Cloudflare settings

In the Cloudflare dashboard for the zone (`yourdomain.com`):

- **SSL/TLS → Overview**: *Full* is fine (tunnel traffic is already encrypted); **Always Use HTTPS** on.
- **SSL/TLS → Edge Certificates**: enable **HSTS** only after confirming https works.
- **Security → Settings**: Bot Fight Mode *off* for this hostname (it can break the PWA's service-worker fetches).
- **Speed → Optimization**: turn **Rocket Loader off** — it rewrites scripts and breaks Svelte hydration.
- **Caching**: default is fine; the app sets its own `Cache-Control` headers.

## 5. Optional: Cloudflare Access (second lock on the door)

Two people use this app. Cloudflare Access (free for up to 50 users) can put
an email one-time-code gate in front of the whole hostname so nobody who is not
one of you two even sees the login page. Defence in depth — the app's own
authentication still applies behind it.

1. **Zero Trust → Access → Applications → Add an application → Self-hosted**.
2. Application name `CouplesCash`, session duration `1 month`, add the public
   hostname `cash.yourdomain.com`.
3. **Identity providers**: keep **One-time PIN** (no OAuth setup required).
4. Add a policy: name `couple`, action **Allow**, include rule
   **Emails** → `you@example.com`, `partner@example.com`.
5. Save. Visiting the site now asks for an email, sends a 6-digit code, and
   sets a Cloudflare cookie for a month. On iOS, do this in Safari *before*
   adding the app to the home screen so the PWA inherits the session.

Caveat: Access cookies are per-browser. If the PWA suddenly shows the
Cloudflare login page instead of the app, the Access session expired — open the
site in Safari/Chrome once, re-authenticate, and the installed app works again.

## 6. Rotating or removing the tunnel

- **New token**: in the tunnel's page → *Configure* → *Refresh token*; paste
  into `.env`; `make tunnel-up`.
- **Remove**: `make tunnel-down`, then delete the tunnel in the dashboard (this
  also removes the CNAME).

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `cloudflared` restarts, log says `Unauthorized` | Token wrong or truncated; copy it again (no quotes, no spaces). |
| Dashboard: tunnel *Healthy*, browser: **502 Bad Gateway** | `web` is not up or unhealthy (`make ps`), or the hostname's URL is not `web:3000`. |
| **Error 1033** | Tunnel is down (Pi off, container stopped, no internet). |
| Login form → **403** | `PUBLIC_URL` ≠ real URL (see §3). |
| Works on Wi-Fi, not on cellular | Not a tunnel issue — check Cloudflare Access session or DNS propagation. |
| Camera (QR scan) blocked | Only works on `https://` origins — never test the scanner over plain `http://<pi-ip>:3000`. |
