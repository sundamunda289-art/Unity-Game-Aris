#!/usr/bin/env python3
"""Owner-only GitHub OIDC bootstrap; candidate credentials never replace the live secret.

Every public line is a fixed marker, validated identity or integer. Raw provider
responses and all credentials stay in private runner files. The workflow is the
sole OIDC trust entry point and remains pinned to canonical main.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shlex
import subprocess
import sys
import time
import urllib.error
import urllib.request

CANONICAL = "sundamunda289-art/Unity-Game-Aris"
BROKER_RE = re.compile(r"https://aaris-turbo-terminal\.[a-z0-9-]+\.workers\.dev\Z")
MACHINE_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9-]{2,119}\Z")
LOGIN_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9-]{0,38}\Z")
SOURCE_RE = re.compile(r"[0-9a-f]{40}\Z")
DEVCONTAINER = ".devcontainer/minimal-ubuntu/devcontainer.json"
TRANSIENT_HTTP = {408, 429, 500, 502, 503, 504}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Failure(Exception):
    def __init__(self, code, http=0):
        self.code, self.http = code, http
        super().__init__(code)


def mark(code, value=None):
    print(code + ("=" + str(value) if value is not None else ""), flush=True)


def run(argv, *, payload=None, check=True, timeout=90):
    try:
        result = subprocess.run(argv, input=payload, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired):
        if check:
            raise Failure("TURBO_PROVIDER_OUTCOME_UNCONFIRMED") from None
        return subprocess.CompletedProcess(argv, 75, "", "")
    if result.returncode and check:
        match = re.search(r"HTTP (\d{3})|\b(401|403|404|409|422|429)\b", result.stderr)
        http = int(next(x for x in match.groups() if x)) if match else 0
        raise Failure("TURBO_PROVIDER_COMMAND_FAILED", http)
    return result


def api(path, method="GET", body=None, check=True):
    argv = ["gh", "api", "--method", method, path]
    if body is not None:
        argv += ["--input", "-"]
    # GET retries are safe. A write is sent once: a lost/5xx response is an
    # unknown outcome, whereas 4xx denial is a concrete failure, never None.
    while True:
        result = run(argv, payload=json.dumps(body) if body is not None else None, check=False)
        if result.returncode:
            match = re.search(r"(?:HTTP[/0-9.]*\s+|\bHTTP\s+)(\d{3})\b", result.stderr)
            http = int(match.group(1)) if match else 0
            if http and http not in TRANSIENT_HTTP:
                raise Failure("TURBO_PROVIDER_REQUEST_REJECTED", http)
            if method != "GET":
                if http == 429:
                    raise Failure("TURBO_PROVIDER_RATE_LIMITED", http)
                if not check:
                    return None
                raise Failure("TURBO_PROVIDER_WRITE_OUTCOME_UNCONFIRMED", http)
            mark("TURBO_WAITING_FOR_PROVIDER_READ")
            time.sleep(10)
            continue
        try:
            data = json.loads(result.stdout or "null")
            if not isinstance(data, dict):
                raise ValueError("object_required")
            return data
        except ValueError:
            if method != "GET" and not check:
                return None
            raise Failure("TURBO_PROVIDER_RESPONSE_UNCONFIRMED") from None


def request(url, *, bearer=None, data=None, attempts=3):
    headers = {"User-Agent": "Mozilla/5.0 Aaris-Setup", "Accept": "application/json"}
    if bearer:
        headers["Authorization"] = "Bearer " + bearer
    if data is not None:
        headers["Content-Type"] = "application/json"
    for attempt in range(attempts):
        try:
            req = urllib.request.Request(url, data=None if data is None else json.dumps(data).encode(), headers=headers)
            with urllib.request.build_opener(NoRedirect()).open(req, timeout=90) as response:
                raw = response.read(1024 * 1024 + 1)
                if len(raw) > 1024 * 1024:
                    raise ValueError("response_too_large")
                result = json.loads(raw)
                if not isinstance(result, dict):
                    raise ValueError("object_required")
                return result
        except urllib.error.HTTPError as exc:
            if exc.code not in (408, 429, 500, 502, 503, 504) or attempt + 1 == attempts:
                raise Failure("TURBO_HTTP_REQUEST_FAILED", exc.code) from None
        except (OSError, ValueError):
            if attempt + 1 == attempts:
                raise Failure("TURBO_NETWORK_OR_RESPONSE_UNCONFIRMED") from None
        time.sleep(2 ** (attempt + 1))


def validate_source(env):
    """Defense in depth; the workflow also validates before checkout/execution."""
    source = env.get("AARIS_SOURCE_SHA", "")
    if not SOURCE_RE.fullmatch(source):
        raise Failure("TURBO_CANONICAL_SOURCE_REQUIRED")
    head = run(["git", "rev-parse", "HEAD"]).stdout.strip()
    if head != source:
        raise Failure("TURBO_CHECKOUT_SOURCE_MISMATCH")
    main = api("repos/" + CANONICAL + "/git/ref/heads/main").get("object", {}).get("sha", "")
    if not SOURCE_RE.fullmatch(main):
        raise Failure("TURBO_CANONICAL_MAIN_UNCONFIRMED")
    comparison = api("repos/" + CANONICAL + "/compare/" + source + "..." + main)
    if comparison.get("status") not in ("ahead", "identical") or comparison.get("merge_base_commit", {}).get("sha") != source:
        raise Failure("TURBO_SOURCE_NOT_CANONICAL_MAIN_HISTORY")
    return source


def verify_minimal_devcontainer(source):
    result = api("repos/" + CANONICAL + "/contents/" + DEVCONTAINER + "?ref=" + source)
    try:
        content = base64.b64decode(result["content"], validate=False)
        local = (Path(__file__).resolve().parents[2] / DEVCONTAINER).read_bytes()
        config = json.loads(content)
    except (KeyError, ValueError, TypeError, OSError):
        raise Failure("TURBO_MINIMAL_DEVCONTAINER_UNCONFIRMED") from None
    hooks = {"initializeCommand", "onCreateCommand", "updateContentCommand", "postCreateCommand", "postAttachCommand"}
    if (content != local or config.get("containerEnv", {}).get("GIT_LFS_SKIP_SMUDGE") != "1"
            or any(config.get(key) for key in hooks)
            or config.get("postStartCommand") != "bash Tools/Turbo/ensure_agent.sh"):
        raise Failure("TURBO_UNITY_FIRST_DEVCONTAINER_REQUIRED")


def identities(env):
    if env.get("GITHUB_REPOSITORY") != CANONICAL or env.get("GITHUB_ACTOR") != "sundamunda289-art":
        raise Failure("TURBO_CANONICAL_OWNER_REQUIRED")
    txn = env.get("INPUT_TRANSACTION_ID", "")
    modern = bool(txn)
    if not txn:
        txn = hashlib.sha256((env.get("GITHUB_RUN_ID", "") + ":" + env.get("GITHUB_RUN_ATTEMPT", "1")).encode()).hexdigest()[:32]
    if not re.fullmatch(r"[0-9a-f]{32}", txn):
        raise Failure("TURBO_INVALID_TRANSACTION")
    candidate = env.get("INPUT_CREDENTIAL_SECRET", "")
    if modern:
        if candidate != "AARIS_STAGE_" + txn.upper():
            raise Failure("TURBO_INVALID_CANDIDATE_SECRET_NAME")
        try:
            credentials = json.loads(env.get("AARIS_CANDIDATE_CREDENTIAL", ""))
        except ValueError:
            raise Failure("TURBO_CANDIDATE_CREDENTIAL_MISSING") from None
        if not isinstance(credentials, dict):
            raise Failure("TURBO_CANDIDATE_CREDENTIAL_INVALID")
        host_token = credentials.get("hosting_token", "")
        agent_token = credentials.get("agent_token", "")
        if (not isinstance(host_token, str) or not re.fullmatch(r"[A-Za-z0-9_]{20,255}", host_token)
                or not isinstance(agent_token, str) or not re.fullmatch(r"[0-9a-f]{64}", agent_token)):
            raise Failure("TURBO_CANDIDATE_CREDENTIAL_INVALID")
        # GitHub command is protocol masking, not a public credential disclosure.
        print("::add-mask::" + host_token, flush=True)
        print("::add-mask::" + agent_token, flush=True)
        os.environ["GH_TOKEN"] = host_token
    else:
        agent_token = secrets.token_hex(32)
        print("::add-mask::" + agent_token, flush=True)
    broker = env.get("INPUT_BROKER", "") or env.get("AARIS_BROKER", "")
    if not BROKER_RE.fullmatch(broker):
        raise Failure("TURBO_INVALID_BROKER")
    expected = env.get("EXPECTED_HOST_LOGIN", "")
    if modern and not LOGIN_RE.fullmatch(expected):
        raise Failure("TURBO_EXPECTED_HOST_REQUIRED")
    if not os.environ.get("GH_TOKEN"):
        raise Failure("TURBO_HOST_CREDENTIAL_MISSING")
    return txn, modern, agent_token, broker, expected


def verified_machine(info):
    machine = info.get("name", "")
    repository = (info.get("repository") or {}).get("full_name", "")
    if not isinstance(machine, str) or not MACHINE_RE.fullmatch(machine):
        raise Failure("TURBO_INVALID_MACHINE_RESPONSE")
    if repository != CANONICAL:
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9-]{0,38}/Unity-Game-Aris", repository):
            raise Failure("TURBO_MACHINE_REPOSITORY_REJECTED")
        details = api("repos/" + repository)
        if not details.get("fork") or CANONICAL not in {
                (details.get("parent") or {}).get("full_name"),
                (details.get("source") or {}).get("full_name")}:
            raise Failure("TURBO_MACHINE_NOT_CANONICAL_FORK")
    return machine


def choose_machine(txn, expected, requested, source):
    user = api("user").get("login", "")
    if not LOGIN_RE.fullmatch(user) or (expected and user != expected):
        raise Failure("TURBO_CODESPACE_ACCOUNT_MISMATCH")
    mark("TURBO_AUTHORIZED_CODESPACE_ACCOUNT", user)
    listing = api("user/codespaces?per_page=100").get("codespaces", [])
    if len(listing) == 100:
        raise Failure("TURBO_MACHINE_LIST_REQUIRES_EXACT_REVIEW")
    eligible = [m for m in listing if (m.get("repository") or {}).get("full_name", "").endswith("/Unity-Game-Aris")]
    if requested and requested not in {"auto", "new"}:
        if not MACHINE_RE.fullmatch(requested):
            raise Failure("TURBO_INVALID_MACHINE")
        selected = [m for m in eligible if m.get("name") == requested]
        if len(selected) != 1:
            raise Failure("TURBO_EXACT_MACHINE_NOT_FOUND")
        return verified_machine(selected[0])
    display = "aaris-setup-" + txn[:16]
    ours = [m for m in eligible if m.get("display_name") == display]
    if len(ours) == 1:
        return verified_machine(ours[0])
    if len(ours) > 1:
        raise Failure("TURBO_DUPLICATE_CREATION_REQUIRES_REVIEW")
    if len(eligible) == 1 and requested != "new":
        return verified_machine(eligible[0])
    if eligible and requested != "new":
        raise Failure("TURBO_EXACT_MACHINE_SELECTION_REQUIRED")
    # Codespace ownership follows this hosting token, independently of repo
    # ownership. No fork or stale fork startup hooks are required.
    verify_minimal_devcontainer(source)
    machines = api("repos/" + CANONICAL + "/codespaces/machines?ref=" + source).get("machines", [])
    if not any(m.get("name") == "standardLinux32gb" for m in machines):
        raise Failure("TURBO_REQUIRED_MACHINE_TYPE_UNAVAILABLE")
    mark("TURBO_CREATE_REQUESTED_ONCE")
    created = api("repos/" + CANONICAL + "/codespaces", "POST", {
        "ref": source, "machine": "standardLinux32gb",
        "devcontainer_path": DEVCONTAINER,
        "display_name": display, "idle_timeout_minutes": 240,
    }, check=False)
    if created and MACHINE_RE.fullmatch(created.get("name", "")):
        return created["name"]
    # Creation may have succeeded despite a lost response. Never create twice.
    while True:
        mark("TURBO_CREATE_OUTCOME_PENDING")
        time.sleep(10)
        candidates = api("user/codespaces?per_page=100").get("codespaces", [])
        ours = [m for m in candidates if m.get("display_name") == display and (m.get("repository") or {}).get("full_name") == CANONICAL]
        if len(ours) == 1:
            return verified_machine(ours[0])
        if len(ours) > 1:
            raise Failure("TURBO_DUPLICATE_CREATION_REQUIRES_REVIEW")


def wait_available(machine, txn, source):
    start_requested = False
    while True:
        info = api("user/codespaces/" + machine)
        state = info.get("state")
        if state == "Available":
            return
        if state in ("Shutdown", "Stopped") and not start_requested:
            # The API cannot prove the on-disk hooks of an old stopped machine.
            # Only resume an operation-owned machine at the reviewed immutable
            # ref. Existing running machines are reused without any restart.
            git = info.get("git_status") or {}
            if (info.get("display_name") != "aaris-setup-" + txn[:16]
                    or (info.get("repository") or {}).get("full_name") != CANONICAL
                    or info.get("devcontainer_path") != DEVCONTAINER
                    or git.get("ref") != source or git.get("has_uncommitted_changes") is not False
                    or git.get("has_unpushed_changes") is not False):
                raise Failure("TURBO_EXISTING_STARTUP_REVIEW_REQUIRED")
            start_requested = True
            api("user/codespaces/" + machine + "/start", "POST", check=False)
        elif state not in ("Shutdown", "Stopped", "ShuttingDown", "Starting", "Queued", "Provisioning", "Rebuilding", "Updating", "Awaiting"):
            raise Failure("TURBO_CODESPACE_UNSAFE_OR_FAILED_STATE")
        mark("TURBO_WAITING_FOR_CODESPACE")
        time.sleep(10)


def oidc():
    url = os.environ["ACTIONS_ID_TOKEN_REQUEST_URL"] + "&audience=aaris-turbo-bootstrap-v1"
    token = request(url, bearer=os.environ["ACTIONS_ID_TOKEN_REQUEST_TOKEN"])["value"]
    try:
        payload = token.split(".")[1]
        claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
    except (KeyError, ValueError, IndexError):
        raise Failure("TURBO_OIDC_INVALID") from None
    required = {"iss": "https://token.actions.githubusercontent.com", "aud": "aaris-turbo-bootstrap-v1",
                "repository": CANONICAL, "repository_id": "1443109769", "actor_id": "353065137",
                "ref": "refs/heads/main", "workflow_ref": CANONICAL + "/.github/workflows/aaris-turbo-bootstrap.yml@refs/heads/main"}
    if any(str(claims.get(k)) != v for k, v in required.items()):
        raise Failure("TURBO_OIDC_CLAIMS_REJECTED")
    if claims.get("event_name") not in ("workflow_dispatch", "issue_comment") or not time.time() < claims.get("exp", 0) <= time.time() + 1800:
        raise Failure("TURBO_OIDC_SCOPE_OR_EXPIRY_REJECTED")
    return token


def remote_invocation(txn, operation):
    if not re.fullmatch(r"[0-9a-f]{32}", txn) or operation not in {"status", "resume"}:
        raise Failure("TURBO_REMOTE_INVOCATION_INVALID")
    code = ("import json,os,pwd,runpy,sys\nfrom pathlib import Path\n"
            "p=Path(pwd.getpwuid(os.geteuid()).pw_dir)/'.local/share/aaris-turbo/transaction_install.py'\n"
            "sys.argv=[str(p),'--" + operation + "','" + txn + "']" +
            ("+['--wait','0']" if operation == "resume" else "") + "\n"
            "if p.is_file():\n runpy.run_path(str(p),run_name='__main__')\nelse:\n"
            " print(json.dumps({'transaction_id':'" + txn + "','state':'NOT_FOUND'}))\n")
    return "python3 -c " + shlex.quote(code)


def install_and_observe(machine, txn, generation, payload, modern):
    """Replay only immutable local staging, never Codespace creation.

    SSH output is untrusted status transport: successful exit, exact transaction,
    machine and generation must agree before a fixed success marker is emitted.
    """
    ssh = ["gh", "codespace", "ssh", "-c", machine, "--"]
    installed = run(ssh + ["bash -s"], payload=payload, check=False, timeout=240)
    status_cmd = remote_invocation(txn,"status")
    target = "PREPARED" if modern else "READY"
    failures = {"FAILED", "ABORTED", "ROLLED_BACK", "ROLLBACK_FAILED", "RECONCILE_REQUIRED"}
    while True:
        checked = run(ssh + [status_cmd], check=False, timeout=45)
        result = None
        if checked.returncode == 0:
            try:
                result = json.loads(checked.stdout.strip())
            except ValueError:
                raise Failure("TURBO_REMOTE_STATUS_INVALID") from None
            if not isinstance(result, dict) or result.get("transaction_id") != txn:
                raise Failure("TURBO_REMOTE_TRANSACTION_MISMATCH")
            state = result.get("state", "")
            if state == "NOT_FOUND":
                if installed.returncode not in (0, 75):
                    raise Failure("TURBO_INSTALL_FAILED_BEFORE_STAGING")
                # Confirmed absence is safe to retry: exact txn and every byte
                # are unchanged; stage() rejects mismatched immutable payloads.
                mark("TURBO_REPLAYING_IMMUTABLE_STAGE")
                installed = run(ssh + ["bash -s"], payload=payload, check=False, timeout=240)
            else:
                if (result.get("machine") != machine or type(result.get("generation")) is not int
                        or result.get("generation") != generation):
                    raise Failure("TURBO_REMOTE_GENERATION_MISMATCH")
                if state == target or (modern and state == "READY"):
                    mark("TURBO_TRANSACTION_" + state)
                    return 0
                if state in failures:
                    raise Failure("TURBO_TRANSACTION_" + state)
                if state not in {"STAGED", "WAITING_FOR_NETWORK", "DRAINING", "CUTOVER_REQUESTED", "VERIFYING_AGENT", "PREPARED", "ROLLING_BACK"}:
                    raise Failure("TURBO_REMOTE_STATE_UNRECOGNIZED")
                # Reconcile a detached supervisor crash without changing intent.
                run(ssh + [remote_invocation(txn,"resume")], check=False, timeout=45)
        mark("TURBO_TRANSACTION_PENDING")
        time.sleep(10)


def main():
    os.umask(0o077)
    txn, modern, token, broker, expected = identities(dict(os.environ))
    source = validate_source(dict(os.environ))
    mark("TURBO_TRANSACTION_ID", txn)
    health = request(broker + "/health")
    if health.get("service") != "aaris-turbo" or health.get("transaction_protocol") != 1:
        raise Failure("TURBO_BROKER_UPGRADE_REQUIRED_RUN_CCF")
    requested = os.environ.get("INPUT_MACHINE", "auto") or "auto"
    issue = os.environ.get("ISSUE_BODY", "")
    if issue:
        if not issue.startswith("TURBO START"):
            raise Failure("TURBO_USE_SAFE_START_NOT_IMPLICIT_RESTART")
        requested = issue[len("TURBO START"):].strip() or "auto"
    machine = choose_machine(txn, expected, requested, source)
    if not MACHINE_RE.fullmatch(machine):
        raise Failure("TURBO_INVALID_MACHINE_RESPONSE")
    mark("TURBO_MACHINE", machine)
    wait_available(machine, txn, source)
    request_data = {"machine": machine, "mode": "prepare", "transaction_id": txn, "token": token}
    grant = request(broker + "/bootstrap", bearer=oidc(), data=request_data)
    if grant.get("machine") != machine or grant.get("transaction_id") != txn or grant.get("endpoint") != broker + "/agent/" + machine:
        raise Failure("TURBO_CANDIDATE_GRANT_MISMATCH")
    if type(grant.get("generation")) is not int or grant["generation"] < 1:
        raise Failure("TURBO_CANDIDATE_GENERATION_INVALID")
    if type(grant.get("expires_at")) is not int or grant["expires_at"] <= time.time() + 60:
        raise Failure("TURBO_CANDIDATE_EXPIRY_INVALID")
    root = Path(__file__).resolve().parent
    values = {"AARIS_AGENT_TOKEN": token, "AARIS_ENDPOINT": grant["endpoint"],
              "AARIS_AGENT_EXPIRES_AT": str(grant["expires_at"]), "AARIS_TRANSACTION_ID": txn,
              "AARIS_AGENT_GENERATION": str(grant["generation"]),
              "AARIS_TRANSACTION_PREPARE_ONLY": "1" if modern else "0"}
    for name, filename in (("AGENT", "agent.py"), ("GUI", "gui_agent.py"), ("GUI_SETUP", "gui_setup.sh"), ("TRANSACTION_INSTALL", "transaction_install.py")):
        values["AARIS_" + name + "_BASE64"] = base64.b64encode((root / filename).read_bytes()).decode()
    payload = "\n".join("export " + k + "=" + shlex.quote(v) for k, v in values.items()) + "\n" + (root / "remote_install.sh").read_text()
    return install_and_observe(machine, txn, grant["generation"], payload, modern)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Failure as exc:
        mark(exc.code)
        if exc.http:
            mark("TURBO_HTTP_STATUS", exc.http)
        raise SystemExit(1)
