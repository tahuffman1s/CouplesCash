#!/usr/bin/env python3
"""Fake SimpleFIN Bridge for the smoke test (protocol: https://www.simplefin.org/protocol.html, v1 shape).

  POST /simplefin/claim/<token>  -> 200, body = access URL with Basic-auth userinfo pointing back at this server
                                    (token "expired" -> 403, like an already-claimed token)
  GET  /simplefin/accounts       -> 200 JSON { errors: [], accounts: [...] }; needs Basic auth user:pass (else 403);
                                    honours start-date / end-date (exclusive) / pending=1
  POST /__phase/<n>              -> enables a data-set phase (phases accumulate): 2 adds a second RENT PAYMENT so auto-match can be
                                    observed; 3 adds "JIFFY LUBE #42" -54.20 today, which clears a planned $60 one-off (v4)
  GET  /__health                 -> 200 ok

Usage: fake-simplefin.py --port 8765 --rent-date YYYY-MM-DD --rent2-date YYYY-MM-DD [--today YYYY-MM-DD]
"""
import argparse
import base64
import json
import sys
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

USER, PASSWORD = "demo-user", "demo-pass"
STATE = {"phases": {1}}


def noon(date_str):
    d = datetime.strptime(date_str, "%Y-%m-%d").replace(hour=12, tzinfo=timezone.utc)
    return int(d.timestamp())


def accounts(args):
    today = noon(args.today)
    rent = noon(args.rent_date)
    checking_tx = [
        {"id": "tx-rent-1", "posted": rent, "amount": "-1850.00", "description": "RENT PAYMENT"},
        {"id": "tx-kroger-1", "posted": today, "amount": "-12.49", "description": "KROGER #123", "payee": "Kroger", "memo": "card 1234"},
        {"id": "tx-payroll-1", "posted": today, "amount": "3200.00", "description": "ACME CORP PAYROLL DIRECT DEP"},
        {"id": "tx-coffee-1", "posted": today, "amount": "-4.50", "description": "SQ *BLUE BOTTLE COFFEE", "pending": True},
    ]
    if 2 in STATE["phases"]:
        checking_tx.append({"id": "tx-rent-2", "posted": noon(args.rent2_date), "amount": "-1850.00", "description": "RENT PAYMENT"})
    if 3 in STATE["phases"]:
        checking_tx.append({"id": "tx-jiffy-1", "posted": today, "amount": "-54.20", "description": "JIFFY LUBE #42", "payee": "Jiffy Lube"})
    return [
        {
            "org": {"domain": "demobank.example", "name": "Demo Bank", "url": "https://demobank.example"},
            "id": "ACT-CHECKING",
            "name": "Checking",
            "currency": "USD",
            "balance": "2450.50",
            "available-balance": "2400.00",
            "balance-date": today,
            "transactions": checking_tx,
        },
        {
            "org": {"domain": "demobank.example", "name": "Demo Bank", "url": "https://demobank.example"},
            "id": "ACT-VISA",
            "name": "Visa Signature",
            "currency": "USD",
            "balance": "-540.00",
            "balance-date": today,
            "transactions": [
                {"id": "tx-netflix-1", "posted": today, "amount": "-15.49", "description": "NETFLIX.COM"},
            ],
        },
    ]


class Handler(BaseHTTPRequestHandler):
    args = None

    def log_message(self, fmt, *a):  # quiet; stderr only on demand
        if "-v" in sys.argv:
            sys.stderr.write("fake-simplefin: " + fmt % a + "\n")

    def _send(self, status, body, ctype="text/plain"):
        data = body.encode() if isinstance(body, str) else body
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        url = urlparse(self.path)
        if url.path == "/__health":
            return self._send(200, "ok")
        if url.path != "/simplefin/accounts":
            return self._send(404, "not found")

        auth = self.headers.get("Authorization", "")
        expected = "Basic " + base64.b64encode(f"{USER}:{PASSWORD}".encode()).decode()
        if auth != expected:
            return self._send(403, "forbidden")

        q = parse_qs(url.query)
        start = int(q.get("start-date", ["0"])[0])
        end = int(q.get("end-date", [str(2**40)])[0])
        pending = q.get("pending", ["0"])[0] == "1"

        result = []
        for acct in accounts(self.args):
            txs = [t for t in acct["transactions"] if start <= t["posted"] < end and (pending or not t.get("pending", False))]
            result.append({**acct, "transactions": txs})
        return self._send(200, json.dumps({"errors": [], "accounts": result}), "application/json")

    def do_POST(self):
        url = urlparse(self.path)
        length = int(self.headers.get("Content-Length") or 0)
        if length:
            self.rfile.read(length)

        if url.path.startswith("/__phase/"):
            STATE["phases"].add(int(url.path.rsplit("/", 1)[1]))
            return self._send(200, "phases " + ",".join(str(p) for p in sorted(STATE["phases"])))

        if url.path.startswith("/simplefin/claim/"):
            token = url.path.rsplit("/", 1)[1]
            if token == "expired":
                return self._send(403, "forbidden")
            host = self.headers.get("Host") or f"127.0.0.1:{self.args.port}"
            return self._send(200, f"http://{USER}:{PASSWORD}@{host}/simplefin")

        return self._send(404, "not found")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--today", default=datetime.now(timezone.utc).strftime("%Y-%m-%d"))
    parser.add_argument("--rent-date", default=None)
    parser.add_argument("--rent2-date", default=None)
    parser.add_argument("-v", action="store_true")
    args = parser.parse_args()
    args.rent_date = args.rent_date or args.today
    args.rent2_date = args.rent2_date or args.today
    Handler.args = args
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    sys.stderr.write(f"fake-simplefin listening on http://127.0.0.1:{args.port} (today {args.today}, rent {args.rent_date}, rent2 {args.rent2_date})\n")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
