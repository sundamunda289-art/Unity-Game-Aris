#!/usr/bin/env python3
"""Classify Wrangler OAuth errors without exposing device codes or tokens.

The diagnosis is intentionally allowlisted. Never print raw Wrangler log text:
it can contain secrets, identifiers, URLs or short-lived authorization codes.
"""
from __future__ import annotations
import pathlib
import re
import sys
from datetime import datetime, timezone

ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
CATEGORIES = (
    ("DEVICE_TIMEOUT", r"device authorization timed out|timed out after 5 minutes|authorization.*expired|expired.*device"),
    ("ACCESS_DENIED", r"access_denied|authorization denied|authorization was denied|user denied|permission denied"),
    ("INVALID_GRANT_OR_CODE", r"invalid_grant|invalid_device_code|incorrect device code|invalid.*device.*code"),
    ("DEVICE_POLLING", r"slow_down|authorization_pending|polling"),
    ("NETWORK_OR_TOKEN_EXCHANGE", r"fetch failed|econnreset|econnrefused|etimedout|enotfound|unable to fetch|could not fetch|token.*exchange.*fail|network.*error"),
    ("WRANGLER_PERMISSIONS", r"insufficient scope|missing permissions|insufficient permissions|unauthorized|forbidden"),
    ("SUCCESS_MARKER", r"successfully logged in"),
)
_ERROR = re.compile(r"\berror\b|failed|timeout|timed out", re.I)


def classify(raw: bytes) -> tuple[str, str]:
    clean = ANSI.sub("", raw.decode("utf-8", "replace")).lower()
    for name, pattern in CATEGORIES:
        if re.search(pattern, clean, re.I):
            return name, "ERROR_PRESENT" if _ERROR.search(clean) else "NO_ERROR_MARKER"
    return ("UNCLASSIFIED_FAILURE" if _ERROR.search(clean) else "NO_REPORTED_ERROR",
            "ERROR_PRESENT" if _ERROR.search(clean) else "NO_ERROR_MARKER")


def latest_log() -> tuple[pathlib.Path, bytes] | None:
    directory = pathlib.Path.home() / ".config" / ".wrangler" / "logs"
    if not directory.is_dir():
        return None
    files = sorted(directory.glob("wrangler-*.log"), key=lambda p: p.stat().st_mtime, reverse=True)
    for path in files[:30]:
        try:
            raw = path.read_bytes()[-300_000:]
        except (OSError, ValueError):
            continue
        # The most recent Wrangler log may be from deploy or whoami rather
        # than OAuth; prefer a log actually mentioning the device flow.
        text = raw.decode("utf-8", "replace").lower()
        if "device authorization" in text or "oauth" in text or "login" in text:
            return path, raw
    return None


def summary(raw: bytes, source: str) -> None:
    kind, marker = classify(raw)
    print("AARIS_CF_OAUTH_DIAG_SOURCE=" + source)
    print("AARIS_CF_OAUTH_DIAG_REASON=" + kind)
    print("AARIS_CF_OAUTH_DIAG_ERROR_MARKER=" + marker)
    print("AARIS_CF_OAUTH_DIAG_NO_RAW_SECRET_OUTPUT=1")


def selftest() -> None:
    cases = (
        (b"Device authorization timed out after 5 minutes.", "DEVICE_TIMEOUT"),
        (b"Token grant rejected: invalid_grant", "INVALID_GRANT_OR_CODE"),
        (b"Error: access_denied", "ACCESS_DENIED"),
        (b"Failed to fetch: ECONNRESET", "NETWORK_OR_TOKEN_EXCHANGE"),
        (b"Successfully logged in.", "SUCCESS_MARKER"),
        (b"\x1b[31mError\x1b[0m: unexpected failure", "UNCLASSIFIED_FAILURE"),
    )
    for raw, want in cases:
        got, _ = classify(raw)
        assert got == want, (got, want)
    print("AARIS_CF_OAUTH_DIAG_SELFTEST_PASS")


if __name__ == "__main__":
    if len(sys.argv) == 2 and sys.argv[1] == "--selftest":
        selftest()
    elif len(sys.argv) == 3 and sys.argv[1] == "--log":
        summary(pathlib.Path(sys.argv[2]).read_bytes()[-300_000:], "CURRENT_PRIVATE_LOGIN")
    elif len(sys.argv) == 2 and sys.argv[1] == "--latest":
        result = latest_log()
        if result is None:
            print("AARIS_CF_OAUTH_DIAG_NO_WRANGLER_LOG_FOUND")
        else:
            path, data = result
            age = max(0, int(datetime.now(timezone.utc).timestamp() - path.stat().st_mtime))
            print("AARIS_CF_OAUTH_DIAG_AGE_SECONDS=" + str(age))
            summary(data, "RECENT_WRANGLER_LOG")
    else:
        sys.exit("usage: wrangler_auth_diag.py --latest | --log PATH | --selftest")
