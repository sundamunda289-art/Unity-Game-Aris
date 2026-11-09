#!/usr/bin/env python3
"""Strict owner-only Issue #77 CS SNAP parser. Emits safe base64 payload, not shell."""
import base64
import json
import re
import sys
from snapshot_cli import normalize_shot

ID = re.compile(r"[A-Za-z][A-Za-z0-9_-]{0,47}\Z", re.ASCII)


def parse(comment):
    prefix="CS SNAP "
    if not isinstance(comment,str) or not comment.startswith(prefix):
        raise ValueError("EXPECTED_CS_SNAP_JSON")
    body=comment[len(prefix):]
    if not 1<=len(body.encode("utf-8"))<=12000:
        raise ValueError("SNAP_PAYLOAD_SIZE_INVALID")
    obj=json.loads(body)
    if not isinstance(obj,dict):
        raise ValueError("SNAP_PAYLOAD_NOT_OBJECT")
    if set(obj)=={"recapture"}:
        name=obj["recapture"]
        if not isinstance(name,str) or not ID.fullmatch(name):
            raise ValueError("SNAP_RECAPTURE_ID_INVALID")
        clean={"recapture":name}
    elif set(obj)=={"shots"}:
        arr=obj["shots"]
        if not isinstance(arr,list) or not 1<=len(arr)<=20:
            raise ValueError("SNAP_COUNT_INVALID")
        shots=[normalize_shot(s) for s in arr]
        if len({s["id"] for s in shots})!=len(shots):
            raise ValueError("SNAP_IDS_DUPLICATE")
        clean={"shots":shots}
    else:
        raise ValueError("SNAP_KEYS_INVALID")
    data=json.dumps(clean,separators=(",",":"),allow_nan=False).encode("ascii")
    return base64.urlsafe_b64encode(data).decode("ascii")


def selftest():
    assert parse('CS SNAP {"shots":[{"id":"forest","pos":[1,2,3],"lookAt":[2,3,4]}]}')
    assert parse('CS SNAP {"recapture":"forest"}')
    for bad in ('CS SNAP ../pwd',
                'CS SNAP {"recapture":"../../evil"}',
                'CS SNAP {"shots":[]}',
                'CS SNAP {"shots":[{"id":"a","pos":[1,2,3],"lookAt":[1,2,3]}]}',
                'CS SNAP {"shots":[{"id":"a","pos":[1,2,3],"lookAt":[2,2,3],"evil":"x"}]}'):
        try:
            parse(bad)
        except (ValueError,json.JSONDecodeError):
            continue
        raise AssertionError(bad)
    print("AARIS_ISSUE_SNAPSHOT_PARSER_PASS")


if __name__=="__main__":
    if sys.argv[1:]==["--selftest"]:
        selftest()
    elif len(sys.argv)==2:
        try:
            print(parse(sys.argv[1]))
        except (ValueError,TypeError,json.JSONDecodeError) as exc:
            print("SNAPSHOT_REJECTED:"+str(exc),file=sys.stderr)
            raise SystemExit(2)
    else:
        raise SystemExit(2)
