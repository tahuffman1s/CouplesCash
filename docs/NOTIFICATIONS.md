# Push notifications

CouplesCash can tap you on the shoulder: *"Electric is due tomorrow · $142.50"*
on both phones a day or two before a bill, *"Rent auto-paid · $1,850"* when the
autopay job marks something paid, and (optionally) *"Kaylee marked Rent paid"*
when your partner changes something. This page explains how that works, how to
turn it on per device, and what to check when a reminder does not arrive.

## How it works, end to end

```
 api container                       push service                   phone
 ┌────────────────────┐   HTTPS      ┌──────────────────┐           ┌──────────────────────┐
 │ SchedulerService   │ ───────────▶ │ Apple / Google / │ ────────▶ │ service worker       │
 │ (ticks every 60 s) │  VAPID-signed│ Mozilla          │  wakes    │ src/service-worker/  │
 │ autopay · reminders│  encrypted   └──────────────────┘  the app  │ shows notification,  │
 │ · hygiene          │  payload                                    │ tap opens `url`      │
 └────────────────────┘                                             └──────────────────────┘
          ▲ reads push_subscriptions, notification_prefs, reminder_log
```

1. **Subscribe.** When you enable notifications in *Settings → Notifications*, the
   browser asks the API for the VAPID *public* key (`GET /v1/push/vapid-public-key`),
   creates a push subscription with the platform's push service, and the app stores it
   (`POST /v1/push/subscriptions`). One subscription per device per account.
2. **Decide.** A hosted service inside the `api` container (`SchedulerService`) wakes every
   60 seconds. For each household it works out "today" and the current hour **in the
   household's time zone** (*Settings → Household → Time zone*, auto-set from the browser).
   - **Reminders**: for each member with reminders on, once per hour at their chosen
     `hour_local`, every unpaid occurrence due in `days_before` days gets one push. A
     `reminder_log` row keeps the same reminder from being sent twice, even across restarts.
   - **Autopay**: items marked *autoPay* with an occurrence due today and no payment yet are
     marked paid (`auto = true`, no actor), an activity event is written, and members with
     *partner activity* on get a push.
   - **Hygiene**: expired passkey challenges are deleted; subscriptions that failed five
     times in a row (or that the push service reports as gone, 404/410) are removed.
3. **Send.** Each message is a small JSON payload `{ title, body, url, tag, icon }`,
   encrypted to the device's keys (RFC 8291) and authenticated with a VAPID JWT signed by
   the server's private key (RFC 8292). The push service only sees ciphertext.
4. **Show.** The service worker displays the notification; tapping it opens `url`, a
   *relative* path such as `/calendar?m=2026-11&d=2026-11-03`, in the installed app.

The payload never contains tokens, account balances or anything beyond the bill's name and
amount. The server never learns anything about the phone beyond the opaque endpoint URL.

## Requirements

| | Why |
|---|---|
| **HTTPS** — i.e. tunnel mode (`PUBLIC_URL=https://…`) | Browsers only expose the Push API in secure contexts. In LAN mode (`http://<pi-ip>:3000`) the Notifications section is hidden. |
| **VAPID keys** in `.env` (`VAPID_PUBLIC_KEY`, `VAPID_PRIVATE_KEY`, `VAPID_SUBJECT`) | `install.sh` and `make secrets` generate them (`make vapid`). Without them the API answers `503 push_not_configured` and the UI hides itself. |
| **iOS 16.4 or newer, app installed to the Home Screen** | Safari on iOS only delivers Web Push to *installed* web apps. Open the site in Safari → Share → *Add to Home Screen*, then open the icon and enable notifications from there. In a Safari tab the toggle will not appear. |
| **Android: Chrome (or another Chromium browser)** | Works from the browser tab or the installed app. Installing is still recommended. |
| **Correct clock on the Pi** | VAPID tokens and "which hour is it" both depend on time. `install.sh --pi` enables NTP; check `timedatectl`. |

## Enabling notifications on a device

Do this once **per phone** (subscriptions are per device, not per account):

1. Install the app: *Settings → Install app* (iOS: Safari → Share → *Add to Home Screen*).
2. Open the **installed** app → *More → Settings → Notifications*.
3. Tap **Enable on this device** and accept the browser prompt.
4. Tap **Send test notification**. You should see *"CouplesCash notifications are working 🎉"*
   within a few seconds.
5. Choose when to be reminded: *days before* (e.g. 1 and 3) and the *hour* (e.g. 09:00).
   These preferences are per account and apply to all your devices; *partner activity* is a
   separate toggle.

Devices appear in the list on the same page with their browser name and when they last
received a push; remove one there to stop pushes to it (or uninstall the app, which also
drops the subscription on the next failed delivery).

## When reminders are sent

- The hour is evaluated in the **household time zone**, not the Pi's `TZ` and not UTC. If
  the household time zone is wrong, reminders arrive at the wrong hour — fix it under
  *Settings → Household*.
- "Due tomorrow" means due on *today + 1* in that time zone; `days_before` may contain `0`
  for a same-day reminder.
- Each occurrence is reminded at most once per `days_before` value per user. Marking a bill
  paid before the reminder hour suppresses the reminder; marking it unpaid again does not
  resend.
- The scheduler runs inside `api`. Nothing fires while the stack is down; reminders missed
  during downtime are not replayed (the next tick only looks at the current hour).

## Autopay auto-marking and Undo

A bill or subscription marked *autoPay* is paid by your bank without you doing anything, so
CouplesCash marks the occurrence paid for you on its due date (household time zone, shortly
after midnight). The calendar shows an **auto** badge, the activity feed shows
*"Rent auto-paid · $1,850"* with no actor, and members with *partner activity* on get a push.

If the bank did *not* actually pay (card declined, balance too low), open the item and tap
**Undo** / **Mark unpaid** — that is the ordinary unpaid endpoint, and the occurrence goes
back to due. The scheduler only ever marks an occurrence that has never been paid, so an
undone autopay is not re-marked on the next tick — the next automatic payment is the item's
next occurrence.

## Troubleshooting

| Symptom | Cause → fix |
|---|---|
| Notifications section missing in Settings | **LAN/http mode** — push needs `https://` (set up the tunnel), **or** the API returned `503 push_not_configured` because `VAPID_*` are empty in `.env` → `make vapid && make up` (`make pi` on the Pi). |
| iOS: no toggle, or the browser prompt never appears | The app is open in a Safari tab, or iOS < 16.4. Add to Home Screen and open it from the icon. Also check *iOS Settings → Notifications → CouplesCash* is allowed. |
| Test notification works, reminders never arrive | Check *Settings → Household → Time zone* and your reminder hour; make sure the bill is **unpaid** and due in exactly `days_before` days; `make scheduler-logs` shows what the job decided each tick. |
| Android: reminders arrive hours late or only when the app is opened | Battery optimisation is throttling the browser. *Android Settings → Apps → Chrome → Battery → Unrestricted* (and the same for the installed CouplesCash app). Samsung: also disable *Put unused apps to sleep*. |
| A device silently stopped receiving pushes | The subscription was dropped after 5 consecutive failures or a 404/410 from the push service (uninstalled, permissions revoked, browser data cleared), **or** the VAPID keys were rotated. Re-enable on that device. |
| Reminder arrives at the wrong hour | Household time zone wrong, or the Pi's clock is off (`timedatectl`). |
| "Auto-paid" fired for something the bank did not pay | Tap **Undo** on the occurrence, and untick *autoPay* on the item if it should not be automatic. |
| `make scheduler-logs` shows nothing | `SCHEDULER_ENABLED=false` in `.env`, or `api` is not healthy (`make ps`). |

Useful commands on the Pi:

```bash
make scheduler-logs           # only the scheduler's log lines (autopay / reminders / hygiene)
make vapid                    # fill empty VAPID_* in .env (FORCE=1 rotates — every device must re-enable)
grep -E '^VAPID' .env         # keys present? (never paste the private key anywhere)
```

## Privacy and security notes

- Payloads contain the item name, the amount, a relative URL and a tag — nothing else.
  No tokens, no balances, no partner's name beyond what the activity line already shows.
- The VAPID private key lives only in `.env` (`chmod 600`) and the `api` container's
  environment. Rotating it (`make vapid FORCE=1 && make up`) invalidates every
  subscription; each phone has to enable notifications again. See
  [SECURITY.md](./SECURITY.md).
- Disabling notifications on a device deletes its subscription server-side. Changing your
  password does **not** (notifications are not a login), so also remove unknown devices from
  the list if a phone is lost.
