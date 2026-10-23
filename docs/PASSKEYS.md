# Passkeys

A passkey is a sign-in credential stored on your phone (or in iCloud Keychain / Google
Password Manager) and unlocked with Face ID, Touch ID, a fingerprint or the device PIN.
CouplesCash supports them through **WebAuthn**: tap *Sign in with a passkey* on the login
page, confirm with biometrics, done — no email, no password, nothing to type on a phone
keyboard. Your password keeps working as a fallback.

## Why they are safer than a password

- There is nothing to guess or leak: the private key never leaves the device, and the
  server only stores the matching public key (`webauthn_credentials`).
- They are **bound to the site's hostname** (the *relying-party id*, rpId). A phishing page on
  another domain cannot use them.
- Every sign-in answers a fresh server challenge that expires after 5 minutes and is accepted
  once. Replaying a captured response does nothing.
- The server tracks the authenticator's sign counter; a cloned credential is detected.

## Requirements

| | Why |
|---|---|
| **HTTPS** — tunnel mode (`PUBLIC_URL=https://…`) — or `http://localhost` in development | WebAuthn only works in secure contexts. In LAN mode (`http://<pi-ip>:3000`) the Passkeys section and the login button are hidden, and the API answers `503` on the passkey routes. |
| **A stable hostname** | The rpId is the host of `PUBLIC_URL` (`cash.example.com`). If you later move to a different hostname, **every existing passkey stops working** (browsers refuse to use a credential for a different rpId). Register new ones afterwards. Changing only the tunnel token, the Pi or the IP address is fine. |
| A phone/computer with a platform authenticator | iPhone/iPad on iOS 16+, Android 9+ with Google Play services, macOS Safari/Chrome, Windows Hello. Hardware keys (YubiKey) also work. |

The API reads the origin from `WebAuthn__Origin` (compose maps it from `PUBLIC_URL`),
derives the rpId from its host, and names the relying party `CouplesCash`
(`WebAuthn__RelyingPartyName`). To pin a different rpId — for example a parent domain —
set `WebAuthn__RelyingPartyId` explicitly in `compose.yml`; the rpId must be the origin's
host or a registrable parent of it.

## Adding a passkey (one per phone)

1. Sign in with your password on the phone you want to register.
2. *More → Settings → Passkeys → Add passkey*.
3. Give it a name you will recognise later, e.g. *"Travis's iPhone"* or *"Kaylee's Pixel"*.
4. Confirm with Face ID / fingerprint. The passkey appears in the list with its creation
   date; *last used* updates on every sign-in.

Repeat on each device. Passkeys synced by iCloud Keychain or Google Password Manager also
appear on your other devices signed into the same Apple/Google account (the list shows
*synced* / *backed up* for those). You can register as many as you like; the partner
registers their own under their account.

## Signing in

On `/login`, tap **Sign in with a passkey**. The browser shows the passkeys it has for this
site (*discoverable credentials*, so no email is needed), you pick one and confirm. On a
device without a passkey the button still appears when the browser supports WebAuthn; it
simply offers to use a passkey from a nearby phone via QR code.

The password form stays underneath. If a passkey ever misbehaves, sign in with the password
and manage passkeys in Settings.

## If a phone is lost or stolen

1. From another device, sign in and go to *Settings → Passkeys* → **delete** the lost
   phone's passkey. It can no longer be used to sign in (even if someone gets past the
   phone's lock screen).
2. *Settings → Change password*. This revokes every refresh token, so the lost phone's
   session ends within 15 minutes — see [SECURITY.md](./SECURITY.md).
3. *Settings → Notifications* → remove the lost device from the list.
4. Use the vendor's remote wipe.

A passkey synced to iCloud Keychain / Google Password Manager survives on your other
devices; the one you deleted in step 1 is gone from CouplesCash regardless of where copies
live.

## Troubleshooting

| Symptom | Cause → fix |
|---|---|
| No *Passkeys* section in Settings, no button on the login page | Not a secure context (LAN/http mode) → use the tunnel. `GET /v1/auth/passkeys/config` returns `enabled: false`. |
| "This passkey can't be used on this site" / nothing happens | `PUBLIC_URL`'s host changed since the passkey was created (rpId mismatch). Sign in with the password, delete the old passkey, add a new one. Keep the hostname stable. |
| Passkey button appears but sign-in fails with 401 | The challenge expired (more than 5 minutes between opening the page and confirming) or was already used. Reload and try again. |
| Registration says the passkey already exists | That authenticator already has a credential for this site under your account; delete it in Settings first if you want to re-register. |
| iOS asks to "Save a passkey in iCloud Keychain" | Normal: iOS stores passkeys in Keychain. If Keychain is off, iOS 17+ still stores it locally. |
| Works on Wi-Fi, fails on cellular | Not a passkey problem; check the tunnel / Cloudflare Access session. |

## For developers

- Library: [Fido2NetLib](https://github.com/passwordless-lib/fido2-net-lib) on the API,
  `@simplewebauthn/browser` in the web app. Options and responses use the WebAuthn Level 3
  JSON encodings described in [API-CONTRACT.md](./API-CONTRACT.md) §Passkeys.
- In native development `scripts/dev-env.sh` sets `WebAuthn__Origin=http://localhost:5173`;
  `localhost` is a secure context, so passkeys can be tested without TLS (Chrome's
  *WebAuthn* DevTools tab provides a virtual authenticator).
- Challenges live in `webauthn_challenges` for 5 minutes and are deleted when used; the
  scheduler's hygiene pass removes expired ones.
