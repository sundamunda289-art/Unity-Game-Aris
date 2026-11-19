#!/usr/bin/env python3
"""Reject obvious blocking calls inside high-frequency Unity gameplay callbacks.

Static guard only: does not instrument Unity, trace call graphs or measure FPS.
"""
from __future__ import annotations
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT_DIR = ROOT / "Assets/AarisWorld/Scripts"
CALLBACK = re.compile(r"\bvoid\s+(Update|LateUpdate|FixedUpdate|OnGUI)\s*\(\s*\)\s*(\{|=>)")
LITERALS = re.compile(
    r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|'
    r"'(?:\\.|[^'\\])*'",
    re.MULTILINE,
)
BLOCKERS = (
    ("sync Resources.Load/LoadAll",
     re.compile(r"\bResources\s*\.\s*Load(?:All)?\s*(?:<[^>]+>\s*)?\(")),
    ("forced full GC",
     re.compile(r"\b(?:System\s*\.\s*)?GC\s*\.\s*Collect\s*\(")),
    ("blocking wait",
     re.compile(r"\b(?:Thread\s*\.\s*Sleep|WaitForCompletion|"
                r"GetAwaiter\s*\(\s*\)\s*\.\s*GetResult|Task\s*\.\s*WaitAll|"
                r"Task\s*\.\s*WaitAny)\s*\(")),
    ("synchronous disk I/O",
     re.compile(r"\b(?:File|Directory)\s*\.\s*(?:ReadAllText|ReadAllBytes|"
                r"WriteAllText|WriteAllBytes|AppendAllText|Copy|CreateDirectory)\s*\(")),
    ("scene-wide object lookup",
     re.compile(r"\b(?:GameObject\s*\.\s*Find|FindObjectsOfType|"
                r"FindObjectsByType|FindFirstObjectByType|FindAnyObjectByType|"
                r"Resources\s*\.\s*FindObjectsOfTypeAll)\s*(?:<[^>]+>\s*)?\(")),
)


def mask_comments_and_strings(source: str) -> str:
    """Blank out comments and C# quoted strings; retain offsets and newlines."""
    return LITERALS.sub(
        lambda m: re.sub(r"[^\n]", " ", m.group(0)), source
    )


def find_matching_brace(source: str, opener: int) -> int:
    if source[opener] != "{":
        raise ValueError("expected body opener")
    nesting = 0
    for index in range(opener, len(source)):
        char = source[index]
        if char == "{":
            nesting += 1
        elif char == "}":
            nesting -= 1
            if nesting == 0:
                return index + 1
    raise ValueError("unterminated C# callback body")


def check_source(source: str, path: str = "<inline>") -> tuple[list[str], int]:
    cleaned = mask_comments_and_strings(source)
    violations = []
    examined = 0
    for method in CALLBACK.finditer(cleaned):
        examined += 1
        start = method.end()
        if method.group(2) == "{":
            body_end = find_matching_brace(cleaned, start - 1)
        else:
            body_end = cleaned.find(";", start)
            if body_end == -1:
                raise ValueError(f"{path}: unterminated expression-body callback")
            body_end += 1
        body = cleaned[start:body_end]
        for label, pattern in BLOCKERS:
            for match in pattern.finditer(body):
                line = cleaned.count("\n", 0, start + match.start()) + 1
                violations.append(
                    f"{path}:{line}: {method.group(1)} has {label}"
                )
    return violations, examined


def main() -> int:
    sources = sorted(SCRIPT_DIR.rglob("*.cs"))
    if not sources:
        raise SystemExit(f"Missing Unity source directory: {SCRIPT_DIR}")
    violations = []
    total = 0
    for path in sources:
        found, examined = check_source(
            path.read_text(encoding="utf-8"), str(path.relative_to(ROOT))
        )
        total += examined
        violations.extend(found)
    if violations:
        print("\n".join(violations))
        print(f"FAIL: {len(violations)} prohibited hot-callback call(s)")
        return 1
    if total == 0:
        print("FAIL: No Unity frame callbacks were examined")
        return 1
    print(f"PASS: {total} Unity frame callbacks in {len(sources)} gameplay scripts")
    print("Source guard only: calls through helpers, Unity Editor compilation, and phone FPS not proven.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
