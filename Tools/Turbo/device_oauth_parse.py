#!/usr/bin/env python3
"""Parse a private Wrangler device OAuth log into verified URL + one-time code.

Never post a real code to GitHub CI, issues, or public logs. Output is consumed
locally on the owner's Termux phone only.
"""
import pathlib
import re
import sys

DOMAIN = "https://dash.cloudflare.com/oauth2/device"
URL = re.compile(r"https://dash\.cloudflare\.com/oauth2/device(?:/verify)?(?:\?user_code=[A-Za-z0-9_-]+)?")
CODE = re.compile(r"and\s+enter\s+the\s+code\s*:\s*([A-Za-z0-9_-]{6,20})\b", re.I)
ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")


def extract(payload: bytes):
    content = ANSI.sub("", payload.decode("utf-8", "replace")).replace("\r", "\n")
    urls = list(URL.finditer(content))
    if not urls:
        return None
    url = urls[-1].group(0)
    code_match = CODE.search(content)
    code = code_match.group(1) if code_match else None
    prefilled = re.search(r"[?]user_code=([A-Za-z0-9_-]{6,20})$", url)
    if prefilled:
        code = prefilled.group(1)
    if not code:
        return None
    if url == DOMAIN:
        url += "?user_code=" + code
    if url not in (DOMAIN + "/verify", DOMAIN + "?user_code=" + code,
                   DOMAIN + "/verify?user_code=" + code):
        return None
    return url, code


def selftest():
    fixtures = (
        (b"To authorize Wrangler, please visit:\n https://dash.cloudflare.com/oauth2/device/verify \n and enter the code:\n\n   pvrDcLTG  \n", DOMAIN + "/verify", "pvrDcLTG"),
        (b"To authorize Wrangler, please visit:\n https://dash.cloudflare.com/oauth2/device\n and enter the code:\n NAXAKAWp\n", DOMAIN + "?user_code=NAXAKAWp", "NAXAKAWp"),
        (b"Opening link: https://dash.cloudflare.com/oauth2/device?user_code=abcD7Q2q\n", DOMAIN + "?user_code=abcD7Q2q", "abcD7Q2q"),
        (b"\x1b[36mhttps://dash.cloudflare.com/oauth2/device/verify\x1b[0m\r\nand enter the code:\r\n\x1b[1m D3fGhK29 \x1b[0m", DOMAIN + "/verify", "D3fGhK29"),
    )
    for payload, url, code in fixtures:
        assert extract(payload) == (url, code), extract(payload)
    assert extract(b"https://dash.cloudflare.com/oauth2/device/verify\n") is None
    assert extract(b"https://example.com/oauth2/device/verify\nand enter the code: abcD7Q2q") is None
    print("AARIS_CF_DEVICE_PARSE_SELFTEST_PASS")


if __name__ == "__main__":
    if len(sys.argv) == 2 and sys.argv[1] == "--selftest":
        selftest()
    elif len(sys.argv) == 2:
        try:
            found = extract(pathlib.Path(sys.argv[1]).read_bytes())
        except (OSError, ValueError):
            sys.exit(1)
        if not found:
            sys.exit(1)
        print(found[0] + "\t" + found[1])
    else:
        sys.exit("usage: device_oauth_parse.py /path/to/private/log | --selftest")
