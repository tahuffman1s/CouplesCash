#!/usr/bin/env python3
"""Fake Plaid API for the smoke test (https://plaid.com/docs/api/ — only the calls CouplesCash makes).

Every call needs the PLAID-CLIENT-ID and PLAID-SECRET headers (else 400 INVALID_API_KEYS) and a JSON body.
  POST /link/token/create          -> { link_token, expiration }; with access_token (update mode) it also clears "login required"
  POST /item/public_token/exchange -> public_token "public-sandbox-good" -> { access_token, item_id }; anything else 400 INVALID_PUBLIC_TOKEN
  POST /item/get                   -> { item, status.transactions.last_successful_update }
  POST /transactions/get           -> the first call for the Item answers 400 PRODUCT_NOT_READY (like a fresh Item); then
                                      { accounts, transactions (honouring start_date / end_date / count / offset), total_transactions }
  POST /accounts/get               -> { accounts }
  POST /transactions/refresh       -> {} (counted)
  POST /item/remove                -> {} (counted)
  POST /__fake/phase/2             -> the pending "SQ *DINER" -40.00 posts as "THE DINER LLC" -48.00 under a new id with
                                      pending_transaction_id (available balance follows)
  POST /__fake/login-required      -> every Item call answers 400 ITEM_LOGIN_REQUIRED until Link runs in update mode
  GET  /__fake/state               -> { refreshes, removed, updateMode, phase }
  GET  /__health                   -> 200 ok
Access tokens and public tokens never appear in this server's output.

Usage: fake-plaid.py --port 8766 [--today YYYY-MM-DD]
"""
import argparse
import datetime as dt
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ACCESS = "access-sandbox-smoke-0001"
STATE = {"phase": 1, "ready": False, "login_required": False, "refreshes": 0, "removed": 0, "update_mode": 0}


def day(today, delta):
    return (today + dt.timedelta(days=delta)).isoformat()


def accounts():
    available = 2345.67 if STATE["phase"] < 2 else 2297.67
    return [
        {"account_id": "acc-chk", "name": "Plaid Checking", "official_name": "Smart Checking", "mask": "0000",
         "type": "depository", "subtype": "checking",
         "balances": {"available": available, "current": 2408.07, "iso_currency_code": "USD"}},
        {"account_id": "acc-sav", "name": "Plaid Saving", "mask": "1111", "type": "depository", "subtype": "savings",
         "balances": {"available": None, "current": 10000.00, "iso_currency_code": "USD"}},
    ]


def transactions(today):
    rows = [
        {"transaction_id": "t-pay", "account_id": "acc-chk", "amount": -1500.00, "date": day(today, 0), "name": "ACME CORP PAYROLL",
         "pending": False, "pending_transaction_id": None},
        {"transaction_id": "t-coffee", "account_id": "acc-chk", "amount": 4.25, "date": day(today, 0), "name": "Blue Bottle",
         "merchant_name": "Blue Bottle Coffee", "original_description": "SQ *BLUE BOTTLE 0042", "pending": False,
         "pending_transaction_id": None},
    ]
    if STATE["phase"] < 2:
        rows.append({"transaction_id": "p-diner", "account_id": "acc-chk", "amount": 40.00, "date": day(today, 0),
                     "authorized_date": day(today, -1), "name": "SQ *DINER", "pending": True})
    else:
        rows.append({"transaction_id": "t-diner", "account_id": "acc-chk", "amount": 48.00, "date": day(today, 0),
                     "authorized_date": day(today, -1), "name": "THE DINER LLC", "pending": False,
                     "pending_transaction_id": "p-diner"})
    return rows


def error(code, typ="ITEM_ERROR", message=None):
    return 400, {"error_type": typ, "error_code": code, "error_message": message or code.lower().replace("_", " "),
                 "display_message": None, "request_id": "fake"}


def handle(path, body, today):
    if path == "/link/token/create":
        if body.get("access_token"):
            if body["access_token"] != ACCESS:
                return error("INVALID_ACCESS_TOKEN", "INVALID_INPUT")
            STATE["update_mode"] += 1
            STATE["login_required"] = False
        elif body.get("products") != ["transactions"]:
            return error("INVALID_FIELD", "INVALID_REQUEST", "products must be [transactions]")
        return 200, {"link_token": "link-sandbox-smoke", "expiration": (dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=4)).strftime("%Y-%m-%dT%H:%M:%SZ")}
    if path == "/item/public_token/exchange":
        if body.get("public_token") != "public-sandbox-good":
            return error("INVALID_PUBLIC_TOKEN", "INVALID_INPUT")
        return 200, {"access_token": ACCESS, "item_id": "item-smoke-1"}
    if body.get("access_token") != ACCESS:
        return error("INVALID_ACCESS_TOKEN", "INVALID_INPUT")
    if path == "/item/remove":
        STATE["removed"] += 1
        return 200, {"request_id": "fake"}
    if STATE["login_required"]:
        return error("ITEM_LOGIN_REQUIRED")
    if path == "/item/get":
        return 200, {"item": {"item_id": "item-smoke-1", "institution_id": "ins_smoke"},
                     "status": {"transactions": {"last_successful_update": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}}}
    if path == "/accounts/get":
        return 200, {"accounts": accounts()}
    if path == "/transactions/refresh":
        STATE["refreshes"] += 1
        return 200, {"request_id": "fake"}
    if path == "/transactions/get":
        if not STATE["ready"]:
            STATE["ready"] = True
            return error("PRODUCT_NOT_READY")
        start, end = body.get("start_date", "0000-00-00"), body.get("end_date", "9999-99-99")
        options = body.get("options") or {}
        rows = [t for t in transactions(today) if start <= t["date"] <= end]
        offset, count = int(options.get("offset", 0)), int(options.get("count", 100))
        return 200, {"accounts": accounts(), "transactions": rows[offset:offset + count], "total_transactions": len(rows),
                     "item": {"item_id": "item-smoke-1"}}
    return 404, {"error": "not found"}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--today")
    args = parser.parse_args()
    today = dt.date.fromisoformat(args.today) if args.today else dt.datetime.now(dt.timezone.utc).date()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *a):  # quiet: bodies carry tokens
            pass

        def reply(self, status, payload):
            data = json.dumps(payload).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            if self.path == "/__health":
                return self.reply(200, {"ok": True})
            if self.path == "/__fake/state":
                return self.reply(200, {"refreshes": STATE["refreshes"], "removed": STATE["removed"],
                                        "updateMode": STATE["update_mode"], "phase": STATE["phase"]})
            return self.reply(404, {"error": "not found"})

        def do_POST(self):
            length = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(length) if length else b"{}"
            if self.path == "/__fake/phase/2":
                STATE["phase"] = 2
                return self.reply(200, {"phase": 2})
            if self.path == "/__fake/login-required":
                STATE["login_required"] = True
                return self.reply(200, {"loginRequired": True})
            if not self.headers.get("PLAID-CLIENT-ID") or not self.headers.get("PLAID-SECRET"):
                return self.reply(*error("INVALID_API_KEYS", "INVALID_INPUT"))
            try:
                body = json.loads(raw or b"{}")
            except json.JSONDecodeError:
                return self.reply(*error("INVALID_BODY", "INVALID_REQUEST"))
            return self.reply(*handle(self.path, body, today))

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"fake Plaid on :{args.port}", file=sys.stderr, flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
