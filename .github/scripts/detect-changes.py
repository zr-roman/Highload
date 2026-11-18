"""Set backend/frontend outputs based on files changed by the current GitHub event."""

import json
import os
import subprocess
import sys
from pathlib import Path

BACKEND_PREFIXES = ("src/", "tests/", "db/")
BACKEND_FILES = {
    ".dockerignore",
    ".github/dependabot.yml",
    ".github/scripts/detect-changes.py",
    ".github/workflows/backend-ci.yml",
    "Directory.Build.props",
    "Directory.Packages.props",
    "global.json",
    "payments-platform.slnx",
    "deploy/ci-release.py",
}
FRONTEND_PREFIXES = ("frontend/",)
FRONTEND_FILES = {
    ".dockerignore",
    ".github/scripts/detect-changes.py",
    ".github/workflows/frontend-ci.yml",
    "deploy/ci-release.py",
}
ZERO_SHA = "0" * 40


def changed_paths() -> set[str] | None:
    event_name = os.environ["GITHUB_EVENT_NAME"]
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))

    if event_name == "workflow_dispatch":
        return None  # A manual run intentionally verifies both sides.
    if event_name == "pull_request":
        pull_request = event["pull_request"]
        base = pull_request["base"]["sha"]
        head = pull_request["head"]["sha"]
        command = ["git", "diff", "--name-only", "-z", f"{base}...{head}"]
    elif event_name == "push":
        base = event["before"]
        head = event["after"]
        command = (
            ["git", "ls-tree", "-r", "-z", "--name-only", head]
            if base == ZERO_SHA
            else ["git", "diff", "--name-only", "-z", base, head]
        )
    else:
        return None  # Unknown event: run checks conservatively.

    try:
        result = subprocess.run(command, check=True, capture_output=True)
    except subprocess.CalledProcessError as error:
        print(f"Could not compare event commits; running both pipelines: {error}", file=sys.stderr)
        return None

    return {path.decode("utf-8", errors="surrogateescape") for path in result.stdout.split(b"\0") if path}


def matches(paths: set[str] | None, prefixes: tuple[str, ...], exact: set[str]) -> bool:
    if paths is None:
        return True
    return any(path in exact or path.startswith(prefixes) for path in paths)


def main() -> None:
    paths = changed_paths()
    backend = matches(paths, BACKEND_PREFIXES, BACKEND_FILES)
    frontend = matches(paths, FRONTEND_PREFIXES, FRONTEND_FILES)
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"backend={str(backend).lower()}\n")
        output.write(f"frontend={str(frontend).lower()}\n")
    print(f"Changed components: backend={str(backend).lower()}, frontend={str(frontend).lower()}")


if __name__ == "__main__":
    main()
