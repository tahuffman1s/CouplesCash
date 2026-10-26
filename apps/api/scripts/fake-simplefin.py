#!/usr/bin/env python3
"""Fake SimpleFIN Bridge for the smoke test (protocol: https://www.simplefin.org/protocol.html, v1 shape).

  POST /simplefin/claim/<token>  -> 200, body = access URL with Basic-auth userinfo pointing back at this server
                                    (token "expired" -> 403, like an already-claimed token)
  GET  /simplefin/accounts       -> 200 JSON { errors: [], accounts: [...] }; needs Basic auth user:pass (else 403);
                                    honours start-date / end-date (exclusive) / pending=1; like the real Bridge, a range over
                                    45 days gets an advisory in errors and one over 90 days is cut to 90 (also in errors)
  POST /__phase/<n>              -> enables a data-set phase (phases accumulate): 2 adds a second RENT PAYMENT so auto-match can be
                                    observed; 3 adds "JIFFY LUBE #42" -54.20 today, which clears a planned $60 one-off (v4);
                                    v5 pending lifecycle and transfers (a later phase may also remove what an earlier one added):
                                    4 pending "THE DINER" -40.00 (id tx-diner-p1, yesterday) + a pending $1.00 "SHELL OIL" fuel hold 7 days old;
                                    5 both pendings vanish, "THE DINER" posts as tx-diner-d1 -48.00 (tip) and the fill-up posts at -45.12;
                                    6 a savings account appears; 7 checking -500 "ONLINE TRANSFER TO SAV" + savings +500;
                                    8 a second pair -250/+250; 9 one-sided "TRANSFER TO BROKERAGE" -1000 two days ago;
                                    10 another "TRANSFER TO BROKERAGE" -1000 today
  GET  /__health                 -> 200 ok

v6 dataset (a second, independent Bridge user for the matcher smoke; claim token "v6"; phases via POST /__phase/v6/<n>):
  1 (always) one checking account: "ACH DEBIT OAKWOOD PROPERTY MGMT RENT" -1850.00 today, "JIFFY LUBE #42" -54.20 today,
    "NETFLIX.COM 866-579-7172 CA" -15.49 at today-87/-58/-29/today, "SPOTIFY USA" -10.99 at today-60/-30/today,
    pending "STARBUCKS STORE 1234" -5.25 today;
  2 "CITY WATER UTIL" -45.00 today;  3 "PLANET FITNESS" -29.99 today

v7 dataset (their real shape; claim token "v7"; phases via POST /__phase/v7/<n>[?cents=N]):
  1 (always) US Bank checking (balance 2408.07, available 2345.67): "ACME CORP PAYROLL" +1500.00 today, pending ×3 = −62.40
    ("WALMART SUPERCENTER" −25.00 posted on its expected post date 3 days out + transacted_at today, "SHELL OIL 5741" −30.00
    in the v2 shape posted=0 + transacted_at, "STARBUCKS 0091" −7.40);
    Ally High Yield Savings (balance 10000.00, no available-balance): "NELNET STUDENT LOAN" −250.00 today;
    two Ally checking fun accounts: "TARGET 0042" −15.00 today, "SEPHORA 118" −22.00 yesterday;
  2 the Friday fun money: US Bank −20.00 ×2 "ONLINE TRANSFER TO ALLY BANK" + +20.00 on each fun account (balances follow);
  3 the sweep: US Bank −<cents> "ONLINE TRANSFER TO ALLY SAVINGS" + savings +<cents> (balances follow)

Usage: fake-simplefin.py --port 8765 --rent-date YYYY-MM-DD --rent2-date YYYY-MM-DD [--today YYYY-MM-DD]
"""
import argparse
import base64
import json
import sys
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

USER, PASSWORD = "demo-user", "demo-pass"
V6_USER, V6_PASSWORD = "v6-user", "v6-pass"
V7_USER, V7_PASSWORD = "v7-user", "v7-pass"
STATE = {"phases": {1}, "v6": {1}, "v7": {1}, "v7_sweep_cents": 0}


def noon(date_str):
    d = datetime.strptime(date_str, "%Y-%m-%d").replace(hour=12, tzinfo=timezone.utc)
    return int(d.timestamp())


def days_ago(args, n):
    d = datetime.strptime(args.today, "%Y-%m-%d").replace(hour=12, tzinfo=timezone.utc)
    return int(d.timestamp()) - n * 86400


def accounts(args):
    today = noon(args.today)
    rent = noon(args.rent_date)
    phases = STATE["phases"]
    checking_tx = [
        {"id": "tx-rent-1", "posted": rent, "amount": "-1850.00", "description": "RENT PAYMENT"},
        {"id": "tx-kroger-1", "posted": today, "amount": "-12.49", "description": "KROGER #123", "payee": "Kroger", "memo": "card 1234"},
        {"id": "tx-payroll-1", "posted": today, "amount": "3200.00", "description": "ACME CORP PAYROLL DIRECT DEP"},
        {"id": "tx-coffee-1", "posted": today, "amount": "-4.50", "description": "SQ *BLUE BOTTLE COFFEE", "pending": True},
    ]
    savings_tx = []
    if 2 in phases:
        checking_tx.append({"id": "tx-rent-2", "posted": noon(args.rent2_date), "amount": "-1850.00", "description": "RENT PAYMENT"})
    if 3 in phases:
        checking_tx.append({"id": "tx-jiffy-1", "posted": today, "amount": "-54.20", "description": "JIFFY LUBE #42", "payee": "Jiffy Lube"})
    # v5: pending authorizations (phase 4) that post under a new id / fall off the feed (phase 5)
    if 4 in phases and 5 not in phases:
        checking_tx.append({"id": "tx-diner-p1", "posted": days_ago(args, 1), "amount": "-40.00", "description": "THE DINER", "pending": True})
        checking_tx.append({"id": "tx-shell-hold", "posted": days_ago(args, 7), "amount": "-1.00", "description": "SHELL OIL 12345", "pending": True})
    if 5 in phases:
        checking_tx.append({"id": "tx-diner-d1", "posted": today, "amount": "-48.00", "description": "THE DINER"})
        checking_tx.append({"id": "tx-shell-fill", "posted": days_ago(args, 6), "amount": "-45.12", "description": "SHELL OIL 12345"})
    # v5: transfers between linked accounts (phases 7, 8) and one-sided transfers (9, 10)
    if 7 in phases:
        checking_tx.append({"id": "tx-xfer-out-1", "posted": today, "amount": "-500.00", "description": "ONLINE TRANSFER TO SAV"})
        savings_tx.append({"id": "tx-xfer-in-1", "posted": today, "amount": "500.00", "description": "ONLINE TRANSFER FROM CHK"})
    if 8 in phases:
        checking_tx.append({"id": "tx-xfer-out-2", "posted": days_ago(args, 1), "amount": "-250.00", "description": "ONLINE TRANSFER TO SAV"})
        savings_tx.append({"id": "tx-xfer-in-2", "posted": today, "amount": "250.00", "description": "ONLINE TRANSFER FROM CHK"})
    if 9 in phases:
        checking_tx.append({"id": "tx-brokerage-1", "posted": days_ago(args, 2), "amount": "-1000.00", "description": "TRANSFER TO BROKERAGE"})
    if 10 in phases:
        checking_tx.append({"id": "tx-brokerage-2", "posted": today, "amount": "-1000.00", "description": "TRANSFER TO BROKERAGE"})
    result = [
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
    if 6 in phases:
        result.append({
            "org": {"domain": "demobank.example", "name": "Demo Bank", "url": "https://demobank.example"},
            "id": "ACT-SAVINGS",
            "name": "High-Yield Savings",
            "currency": "USD",
            "balance": "10000.00",
            "balance-date": today,
            "transactions": savings_tx,
        })
    return result


def v6_accounts(args):
    today = noon(args.today)
    phases = STATE["v6"]
    tx = [
        {"id": "v6-rent-1", "posted": today, "amount": "-1850.00", "description": "ACH DEBIT OAKWOOD PROPERTY MGMT RENT"},
        {"id": "v6-jiffy-1", "posted": today, "amount": "-54.20", "description": "JIFFY LUBE #42"},
        {"id": "v6-coffee-p1", "posted": today, "amount": "-5.25", "description": "STARBUCKS STORE 1234", "pending": True},
    ]
    for i, ago in enumerate((87, 58, 29, 0)):
        tx.append({"id": f"v6-netflix-{i}", "posted": days_ago(args, ago), "amount": "-15.49", "description": "NETFLIX.COM 866-579-7172 CA"})
    for i, ago in enumerate((60, 30, 0)):
        tx.append({"id": f"v6-spotify-{i}", "posted": days_ago(args, ago), "amount": "-10.99", "description": "SPOTIFY USA"})
    if 2 in phases:
        tx.append({"id": "v6-water-1", "posted": today, "amount": "-45.00", "description": "CITY WATER UTIL"})
    if 3 in phases:
        tx.append({"id": "v6-gym-1", "posted": today, "amount": "-29.99", "description": "PLANET FITNESS"})
    return [
        {
            "org": {"domain": "demobank.example", "name": "Demo Bank", "url": "https://demobank.example"},
            "id": "ACT-V6-CHECKING",
            "name": "Everyday Checking",
            "currency": "USD",
            "balance": "3200.00",
            "available-balance": "3194.75",
            "balance-date": today,
            "transactions": tx,
        }
    ]


def money(cents):
    sign = "-" if cents < 0 else ""
    cents = abs(cents)
    return f"{sign}{cents // 100}.{cents % 100:02d}"


def v7_accounts(args):
    today = noon(args.today)
    yesterday = days_ago(args, 1)
    phases = STATE["v7"]
    ally = {"domain": "ally.com", "name": "Ally Bank", "url": "https://ally.com"}
    usbank = {"domain": "usbank.com", "name": "U.S. Bank", "url": "https://usbank.com"}
    usb_balance, usb_available, sav_balance, fun1_balance, fun2_balance = 240807, 234567, 1000000, 11000, 9500
    usb = [
        {"id": "v7-payroll-1", "posted": today, "amount": "1500.00", "description": "ACME CORP PAYROLL"},
        # dated by the bank on the day it expects to post (3 days out); it happened today
        {"id": "v7-walmart-p1", "posted": days_ago(args, -3), "transacted_at": today, "amount": "-25.00",
         "description": "WALMART SUPERCENTER", "pending": True},
        {"id": "v7-shell-p1", "posted": 0, "transacted_at": yesterday, "amount": "-30.00", "description": "SHELL OIL 5741"},
        {"id": "v7-sbux-p1", "posted": today, "amount": "-7.40", "description": "STARBUCKS 0091", "pending": True},
    ]
    sav = [{"id": "v7-nelnet-1", "posted": today, "amount": "-250.00", "description": "NELNET STUDENT LOAN"}]
    fun1 = [{"id": "v7-target-1", "posted": today, "amount": "-15.00", "description": "TARGET 0042"}]
    fun2 = [{"id": "v7-sephora-1", "posted": yesterday, "amount": "-22.00", "description": "SEPHORA 118"}]
    if 2 in phases:
        usb.append({"id": "v7-fun-out-1", "posted": today, "amount": "-20.00", "description": "ONLINE TRANSFER TO ALLY BANK"})
        usb.append({"id": "v7-fun-out-2", "posted": today, "amount": "-20.00", "description": "ONLINE TRANSFER TO ALLY BANK"})
        fun1.append({"id": "v7-fun-in-1", "posted": today, "amount": "20.00", "description": "TRANSFER FROM US BANK"})
        fun2.append({"id": "v7-fun-in-2", "posted": today, "amount": "20.00", "description": "TRANSFER FROM US BANK"})
        usb_balance -= 4000
        usb_available -= 4000
        fun1_balance += 2000
        fun2_balance += 2000
    if 3 in phases and STATE["v7_sweep_cents"] > 0:
        cents = STATE["v7_sweep_cents"]
        usb.append({"id": "v7-sweep-out-1", "posted": today, "amount": money(-cents), "description": "ONLINE TRANSFER TO ALLY SAVINGS"})
        sav.append({"id": "v7-sweep-in-1", "posted": today, "amount": money(cents), "description": "TRANSFER FROM US BANK"})
        usb_balance -= cents
        usb_available -= cents
        sav_balance += cents
    return [
        {"org": usbank, "id": "ACT-V7-USB", "name": "US Bank Checking", "currency": "USD", "balance": money(usb_balance),
         "available-balance": money(usb_available), "balance-date": today, "transactions": usb},
        {"org": ally, "id": "ACT-V7-SAV", "name": "Ally High Yield Savings", "currency": "USD", "balance": money(sav_balance),
         "balance-date": today, "transactions": sav},
        {"org": ally, "id": "ACT-V7-FUN1", "name": "Ally Spending 1", "currency": "USD", "balance": money(fun1_balance),
         "available-balance": money(fun1_balance), "balance-date": today, "transactions": fun1},
        {"org": ally, "id": "ACT-V7-FUN2", "name": "Ally Spending 2", "currency": "USD", "balance": money(fun2_balance),
         "available-balance": money(fun2_balance), "balance-date": today, "transactions": fun2},
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
        expected_v6 = "Basic " + base64.b64encode(f"{V6_USER}:{V6_PASSWORD}".encode()).decode()
        expected_v7 = "Basic " + base64.b64encode(f"{V7_USER}:{V7_PASSWORD}".encode()).decode()
        if auth not in (expected, expected_v6, expected_v7):
            return self._send(403, "forbidden")
        dataset = v6_accounts if auth == expected_v6 else v7_accounts if auth == expected_v7 else accounts

        q = parse_qs(url.query)
        start = int(q.get("start-date", ["0"])[0])
        end = int(q.get("end-date", [str(2**40)])[0])
        pending = q.get("pending", ["0"])[0] == "1"
        errors = []
        if "start-date" in q:
            days = ((end if "end-date" in q else int(time.time())) - start) / 86400
            if days > 90:
                start = end - 90 * 86400
                errors.append("You requested more than 90 days of transactions; only 90 days were returned.")
            elif days > 45:
                errors.append("Requesting more than 45 days of transactions may not be supported in the future.")

        result = []
        for acct in dataset(self.args):
            # v2-style pending rows carry posted = 0 and are dated by transacted_at
            txs = [t for t in acct["transactions"]
                   if start <= (t.get("posted") or t.get("transacted_at", 0)) < end
                   and (pending or not (t.get("pending", False) or t.get("posted") == 0))]
            result.append({**acct, "transactions": txs})
        return self._send(200, json.dumps({"errors": errors, "accounts": result}), "application/json")

    def do_POST(self):
        url = urlparse(self.path)
        length = int(self.headers.get("Content-Length") or 0)
        if length:
            self.rfile.read(length)

        if url.path.startswith("/__phase/v7/"):
            q = parse_qs(url.query)
            if "cents" in q:
                STATE["v7_sweep_cents"] = int(q["cents"][0])
            STATE["v7"].add(int(url.path.rsplit("/", 1)[1]))
            return self._send(200, "v7 phases " + ",".join(str(p) for p in sorted(STATE["v7"])))

        if url.path.startswith("/__phase/v6/"):
            STATE["v6"].add(int(url.path.rsplit("/", 1)[1]))
            return self._send(200, "v6 phases " + ",".join(str(p) for p in sorted(STATE["v6"])))

        if url.path.startswith("/__phase/"):
            STATE["phases"].add(int(url.path.rsplit("/", 1)[1]))
            return self._send(200, "phases " + ",".join(str(p) for p in sorted(STATE["phases"])))

        if url.path.startswith("/simplefin/claim/"):
            token = url.path.rsplit("/", 1)[1]
            if token == "expired":
                return self._send(403, "forbidden")
            host = self.headers.get("Host") or f"127.0.0.1:{self.args.port}"
            user, password = (V6_USER, V6_PASSWORD) if token == "v6" else (V7_USER, V7_PASSWORD) if token == "v7" else (USER, PASSWORD)
            return self._send(200, f"http://{user}:{password}@{host}/simplefin")

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
