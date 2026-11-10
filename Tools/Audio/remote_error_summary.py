#!/usr/bin/env python3
"""Safe classification of private Codespace SSH failures; no raw credentials."""
import os
import re
import sys
from pathlib import Path

def classify(s: str) -> str:
    if re.search(r"403|forbidden|unauthoriz|permission denied|authentication failed", s, re.I):
        return "AUTHORIZATION"
    if re.search(r"unknown command|invalid argument|unknown flag|usage:", s, re.I):
        return "GH_CLI_SYNTAX"
    if re.search(r"no such file|not found|missing", s, re.I):
        return "REMOTE_MISSING_BINARY"
    if re.search(r"connection|network|ssh|timed out|timeout", s, re.I):
        return "SSH_TRANSPORT"
    if not s.strip():
        return "REMOTE_EXIT_WITHOUT_STDERR"
    return "UNCLASSIFIED_REMOTE_ERROR"

def main():
    if sys.argv[1:] == ["--selftest"]:
        assert classify("HTTP 403: Forbidden") == "AUTHORIZATION"
        assert classify("unknown flag: --foo") == "GH_CLI_SYNTAX"
        assert classify("ssh: connect to host failed") == "SSH_TRANSPORT"
        assert classify("") == "REMOTE_EXIT_WITHOUT_STDERR"
        print("AARIS_AUDIO_SAFE_CLASSIFIER_SELFTEST_PASS")
        return
    if sys.argv[1:] != ["--github-actions"]:
        raise SystemExit(2)
    root = Path(os.environ["RUNNER_TEMP"])
    raw = (root / "aaris-audio-private.txt").read_text(errors="replace")[:80000]
    print("AARIS_AUDIO_REMOTE_DIAGNOSTIC_" + classify(raw))

if __name__ == "__main__":
    main()
