#!/usr/bin/env python3
"""Which services CI releases, and how it writes their new images into deploy/k8s/kustomization.yaml.

    python3 deploy/ci-release.py plan [service-to-exclude...]   # writes matrix=... and services=... to $GITHUB_OUTPUT
    python3 deploy/ci-release.py apply <tag> <service>...       # newTag and newName of those services

A service is released when its sources changed since the commit its current image was built from. The tag in
kustomization.yaml is that commit (deploy/release.ps1 and CI both tag images with the commit), so git answers the
question by itself: no list of the last push, nothing that a cancelled run could make CI forget. A tag that is not a
commit (dev) means the image was never released: such a service is built too.
"""

import json
import os
import re
import subprocess
import sys

REGISTRY = "ghcr.io/zr-roman"
KUSTOMIZATION = "deploy/k8s/kustomization.yaml"

# What each image is built from: the same catalog as in deploy/release.ps1.
BUILD_CONTEXT = [".dockerignore"]
DOTNET_SHARED = ["src/Platform", "src/Contracts", "Directory.Build.props", "Directory.Packages.props"]
CATALOG = {
    "payments-api": {"dockerfile": "src/Payments.Api/Dockerfile", "sources": ["src/Payments.Api", *DOTNET_SHARED, *BUILD_CONTEXT]},
    "ledger-service": {"dockerfile": "src/Ledger.Service/Dockerfile", "sources": ["src/Ledger.Service", *DOTNET_SHARED, *BUILD_CONTEXT]},
    "bank-emulator": {"dockerfile": "src/bank-emulator/Dockerfile", "sources": ["src/bank-emulator", *BUILD_CONTEXT]},
    "antifraud": {"dockerfile": "src/antifraud-service/Dockerfile", "sources": ["src/antifraud-service", *BUILD_CONTEXT]},
    "frontend": {"dockerfile": "frontend/Dockerfile", "sources": ["frontend", *BUILD_CONTEXT]},
}

# An entry of the images block: "- name: <service>", then newTag, then newName if the image is in the registry.
ENTRY = (
    r"^(?P<indent>[ \t]*)- name: {name}[ \t]*\r?\n"
    r"(?P<inner>[ \t]*)newTag:[ \t]*(?P<tag>\S+)"
    r"(?:[ \t]*\r?\n[ \t]*newName:[ \t]*\S+)?"
)


def entry(service: str) -> re.Pattern[str]:
    return re.compile(ENTRY.format(name=re.escape(service)), re.MULTILINE)


def read() -> str:
    with open(KUSTOMIZATION, encoding="utf-8", newline="") as file:
        return file.read()


def built_from(tag: str) -> str | None:
    """The commit the tag names, or None when it is not a commit of this repository."""
    result = subprocess.run(
        ["git", "rev-parse", "--verify", "--quiet", f"{tag}^{{commit}}"], capture_output=True, text=True
    )
    return result.stdout.strip() if result.returncode == 0 else None


def plan(excluded: set[str] | None = None) -> None:
    excluded = excluded or set()
    text = read()
    include = []
    for service, spec in CATALOG.items():
        if service in excluded:
            continue
        match = entry(service).search(text)
        if not match:
            sys.exit(f"No images entry for {service} in {KUSTOMIZATION}")
        tag = match["tag"]
        commit = built_from(tag)
        if commit is None:
            reason = f"tag {tag} is not a commit"
        elif subprocess.run(["git", "diff", "--quiet", commit, "HEAD", "--", *spec["sources"]]).returncode != 0:
            reason = f"sources changed since {tag}"
        else:
            print(f"{service}: up to date at {tag}", file=sys.stderr)
            continue
        print(f"{service}: release ({reason})", file=sys.stderr)
        include.append({"service": service, "dockerfile": spec["dockerfile"]})

    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"matrix={json.dumps({'include': include})}\n")
        output.write(f"services={' '.join(item['service'] for item in include)}\n")


def is_older(tag: str, current: str) -> bool:
    """Whether the commit of tag is not a descendant of the commit of the current image: a run of an older commit
    that finished after a newer one. Runs of four merges a few seconds apart released in reverse order once: the last
    tag written was the oldest commit, and the newest build was silently replaced by an older one."""
    current_commit = built_from(current)
    if current_commit is None:
        return False  # dev: not a release yet, anything is newer
    new_commit = built_from(tag)
    if new_commit is None:
        sys.exit(f"{tag} is not a commit of this repository")
    return subprocess.run(["git", "merge-base", "--is-ancestor", current_commit, new_commit]).returncode != 0


def apply(tag: str, services: list[str]) -> None:
    text = read()
    for service in services:
        pattern = entry(service)
        match = pattern.search(text)
        if not match:
            sys.exit(f"No images entry for {service} in {KUSTOMIZATION}")
        if is_older(tag, match["tag"]):
            print(f"{service}: {match['tag']} is newer than {tag}, left as it is", file=sys.stderr)
            continue
        nl = "\r\n" if "\r\n" in text else "\n"  # CRLF in a Windows working copy, LF in CI
        text = pattern.sub(
            lambda m, s=service: (
                f"{m['indent']}- name: {s}{nl}{m['inner']}newTag: {tag}{nl}{m['inner']}newName: {REGISTRY}/{s}"
            ),
            text,
            count=1,
        )
    with open(KUSTOMIZATION, "w", encoding="utf-8", newline="") as file:
        file.write(text)


if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "plan":
        plan(set(sys.argv[2:]))
    elif len(sys.argv) >= 4 and sys.argv[1] == "apply":
        apply(sys.argv[2], sys.argv[3:])
    else:
        sys.exit(__doc__)
