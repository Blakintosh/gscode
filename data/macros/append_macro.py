#!/usr/bin/env python3
"""Append macro entries to a macro library JSON, keeping it valid and sorted.

Usage:
    python3 append_macro.py t7_macros_gsh.json --entry entry.json
    python3 append_macro.py t7_macros_gsh.json --bulk fragment.json

--entry takes a single entry object; --bulk takes a JSON array of them. Entries
are validated against SCHEMA.md, merged into any existing entry of the same
name (their definitions are unioned), and the array is re-sorted by name,
case-insensitive. revisedOn and revision are bumped on success.
"""

import argparse
import datetime
import json
import sys

KINDS = {"constant", "function", "builtin"}
CONFIDENCES = {"high", "medium", "low"}


def fail(msg):
    print(f"error: {msg}", file=sys.stderr)
    sys.exit(1)


def validate_entry(entry, index):
    where = f"entry {index} ({entry.get('name', '?')!r})"
    if not isinstance(entry, dict):
        fail(f"{where}: not an object")

    unknown = set(entry) - {
        "name", "kind", "description", "definitions",
        "example", "remarks", "flags", "confidence",
    }
    if unknown:
        fail(f"{where}: unknown fields {sorted(unknown)}")

    name = entry.get("name")
    if not isinstance(name, str) or not name:
        fail(f"{where}: 'name' must be a non-empty string")
    if entry.get("kind") not in KINDS:
        fail(f"{where}: 'kind' must be one of {sorted(KINDS)}")
    desc = entry.get("description")
    if not isinstance(desc, str) or not desc.strip():
        fail(f"{where}: 'description' must be a non-empty string")
    if not desc.rstrip().endswith("."):
        fail(f"{where}: 'description' must end with a period")
    if any(ord(c) > 127 for c in json.dumps(entry, ensure_ascii=False)):
        fail(f"{where}: entry must be plain ASCII")

    defs = entry.get("definitions")
    if not isinstance(defs, list):
        fail(f"{where}: 'definitions' must be an array")
    if entry["kind"] == "builtin":
        if defs:
            fail(f"{where}: builtin macros have no definitions")
    elif not defs:
        fail(f"{where}: non-builtin macros need at least one definition")

    for d in defs:
        if not isinstance(d, dict):
            fail(f"{where}: definition is not an object")
        if set(d) != {"path", "line", "parameters", "expansion"}:
            fail(f"{where}: definition fields must be path/line/parameters/expansion")
        if not isinstance(d["path"], str) or not d["path"].startswith("scripts/"):
            fail(f"{where}: definition path must be relative, starting 'scripts/'")
        if not isinstance(d["line"], int) or d["line"] < 1:
            fail(f"{where}: definition line must be a positive integer")
        if not isinstance(d["expansion"], str):
            fail(f"{where}: expansion must be a string")
        params = d["parameters"]
        if entry["kind"] == "function":
            if not isinstance(params, list) or not params:
                fail(f"{where}: function macro definitions need a parameters array")
            for p in params:
                if (not isinstance(p, dict) or set(p) != {"name", "description"}
                        or not isinstance(p["name"], str)
                        or not isinstance(p["description"], str)):
                    fail(f"{where}: parameters must be {{name, description}} objects")
        elif params is not None:
            fail(f"{where}: parameters must be null for {entry['kind']} macros")

    for field in ("example", "remarks"):
        if not (entry.get(field) is None or isinstance(entry.get(field), str)):
            fail(f"{where}: '{field}' must be a string or null")
    flags = entry.get("flags")
    if not (isinstance(flags, list) and all(isinstance(f, str) for f in flags)):
        fail(f"{where}: 'flags' must be an array of strings")
    if entry.get("confidence") not in CONFIDENCES:
        fail(f"{where}: 'confidence' must be one of {sorted(CONFIDENCES)}")


def merge(existing, incoming):
    """Merge incoming into the existing entry of the same name."""
    seen = {(d["path"], d["line"]) for d in existing["definitions"]}
    for d in incoming["definitions"]:
        if (d["path"], d["line"]) not in seen:
            existing["definitions"].append(d)
    existing["definitions"].sort(key=lambda d: (d["path"], d["line"]))
    # Prefer the longer prose on the assumption it carries more information.
    for field in ("description", "remarks", "example"):
        if len(incoming.get(field) or "") > len(existing.get(field) or ""):
            existing[field] = incoming[field]
    existing["flags"] = sorted(set(existing["flags"]) | set(incoming["flags"]))
    order = ["low", "medium", "high"]
    existing["confidence"] = min(
        existing["confidence"], incoming["confidence"], key=order.index)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("library")
    group = ap.add_mutually_exclusive_group(required=True)
    group.add_argument("--entry", help="JSON file holding one entry object")
    group.add_argument("--bulk", help="JSON file holding an array of entries")
    args = ap.parse_args()

    with open(args.library, encoding="ascii") as f:
        lib = json.load(f)
    src = args.entry or args.bulk
    with open(src, encoding="utf-8") as f:
        incoming = json.load(f)
    if args.entry:
        incoming = [incoming]
    if not isinstance(incoming, list):
        fail("--bulk input must be a JSON array")

    for i, entry in enumerate(incoming):
        validate_entry(entry, i)

    by_name = {e["name"]: e for e in lib["macros"]}
    added = merged = 0
    for entry in incoming:
        if entry["name"] in by_name:
            merge(by_name[entry["name"]], entry)
            merged += 1
        else:
            by_name[entry["name"]] = entry
            added += 1

    lib["macros"] = sorted(by_name.values(), key=lambda e: (e["name"].lower(), e["name"]))
    lib["revisedOn"] = datetime.datetime.now(datetime.timezone.utc).isoformat(
        timespec="milliseconds").replace("+00:00", "Z")
    lib["revision"] = lib.get("revision", 0) + 1

    with open(args.library, "w", encoding="ascii") as f:
        json.dump(lib, f, indent=1, ensure_ascii=True)
        f.write("\n")
    print(f"{args.library}: {added} added, {merged} merged, "
          f"{len(lib['macros'])} total (revision {lib['revision']})")


if __name__ == "__main__":
    main()
