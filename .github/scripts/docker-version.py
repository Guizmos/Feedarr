#!/usr/bin/env python3
"""Resolve Docker versions without changing source files or creating tags."""
import os
import re
import subprocess
import sys

STABLE = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", re.ASCII)


def resolve(channel, tags, ref, run_id, sha):
    versions = {
        tag: tuple(map(int, match.groups()))
        for tag in tags
        if (match := STABLE.fullmatch(tag))
    }
    if channel == "stable":
        if ref not in versions:
            raise ValueError("Stable tag must match vX.Y.Z without leading zeros")
        if versions[ref] <= max((v for t, v in versions.items() if t != ref), default=(-1, -1, -1)):
            raise ValueError("Stable version must exceed every previous stable tag")
        return ref[1:]
    if channel != "beta" or not run_id.isdecimal() or int(run_id) < 1:
        raise ValueError("Expected beta channel with a positive GitHub run ID")
    major, minor, patch = max(versions.values(), default=(0, 0, 0))
    return f"{major}.{minor}.{patch + 1}-beta.{run_id}.sha.{sha[:7]}"


if __name__ == "__main__":
    tags = subprocess.check_output(["git", "tag", "--list"], text=True).splitlines()
    try:
        version = resolve(sys.argv[1], tags, os.environ["GITHUB_REF_NAME"],
                          os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_SHA"])
    except (ValueError, KeyError, IndexError) as error:
        sys.exit(str(error))
    print(f"version={version}")
