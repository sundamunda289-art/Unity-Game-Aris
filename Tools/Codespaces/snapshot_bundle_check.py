#!/usr/bin/env python3
"""Verify owner-only real Unity screenshot archive; no unsafe tar extraction."""
import json
from pathlib import Path
import re
import sys
import tarfile

ID = re.compile(r"[A-Za-z][A-Za-z0-9_-]{0,47}\Z", re.ASCII)


def verify(archive, out):
    archive, out = Path(archive), Path(out)
    if not archive.is_file() or archive.stat().st_size > 110000000:
        raise ValueError("snapshot archive size invalid")
    with tarfile.open(archive, "r:gz") as tar:
        members = tar.getmembers()
        files = {m.name: m for m in members if m.isfile()}
        if "result.json" not in files or len(files) > 21:
            raise ValueError("invalid archive members")
        if any(not (m.isfile() or m.isdir()) or m.size > 12*1024*1024
               for m in members):
            raise ValueError("unsafe archive entry")
        if files["result.json"].size > 200000:
            raise ValueError("result JSON too large")
        handle = tar.extractfile(files["result.json"])
        if handle is None:
            raise ValueError("metadata missing")
        result = json.loads(handle.read())
        frames = result.get("frames")
        if result.get("state") not in ("passed", "suspect") or not isinstance(frames,list):
            raise ValueError("not a verified Unity image bundle")
        if not 1 <= len(frames) <= 20:
            raise ValueError("invalid number of frames")
        expected = {"result.json"}
        for f in frames:
            name=f.get("id")
            if not isinstance(name,str) or not ID.fullmatch(name):
                raise ValueError("unsafe shot identifier")
            if f.get("state") not in ("passed","suspect"):
                raise ValueError("unverified frame in archive")
            pathname="images/"+name+".png"
            expected.add(pathname)
            if pathname not in files or files[pathname].size < 5000:
                raise ValueError("missing real PNG")
        if set(files) != expected:
            raise ValueError("unexpected archive data")
        # Verify *all* files before writing any untrusted archive content.
        extracted={}
        for pathname in expected-{"result.json"}:
            stream=tar.extractfile(files[pathname])
            if stream is None:
                raise ValueError("PNG stream missing")
            data=stream.read(12*1024*1024+1)
            if len(data)!=files[pathname].size or data[:8]!=b"\x89PNG\r\n\x1a\n":
                raise ValueError("corrupt or non-PNG data")
            extracted[Path(pathname).name]=data
        out.mkdir(parents=True,exist_ok=True)
        (out/"snapshot-result.json").write_text(json.dumps(result,indent=2)+"\n")
        for filename,data in extracted.items():
            (out/filename).write_bytes(data)
        return result["state"],len(frames)


if __name__=="__main__":
    if len(sys.argv)!=3:
        raise SystemExit(2)
    state,count=verify(sys.argv[1],sys.argv[2])
    print("AARIS_SNAPSHOT_BUNDLE_"+state.upper()+"_COUNT_"+str(count))
