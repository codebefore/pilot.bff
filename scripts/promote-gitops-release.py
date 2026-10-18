#!/usr/bin/env python3
"""Record a verified BFF image on its dedicated GitOps branch."""
import json
import os
import re
import subprocess
import time
from pathlib import Path

BRANCH = "gitops/bff-production"


def command(args, **kwargs):
    result = subprocess.run(args, capture_output=True, text=True, timeout=60, **kwargs)
    if result.returncode:
        raise RuntimeError("GitOps operation failed; raw output suppressed")
    return result.stdout.strip()


def release_record(previous, sha, digest, run_number, run_url):
    if not re.fullmatch(r"[0-9a-f]{40}", sha) or not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
        raise RuntimeError("Invalid immutable revision/digest")
    if int(previous.get("workflowRunNumber", 0)) > run_number:
        return None
    if previous.get("bffRevision") == sha and previous.get("imageDigest") == digest:
        return None
    return dict(
        previous,
        bffRevision=sha,
        imageRepository="ghcr.io/codebefore/pilot-bff",
        imageTag="sha-" + sha,
        imageDigest=digest,
        imagePullSecret="ghcr-pull-secret",
        workflowRunNumber=run_number,
        workflowRunUrl=run_url,
    )


def image_values(record):
    # JSON is valid YAML and keeps the exact image digest tied to valuesRevision.
    return json.dumps(
        {
            "image": {
                "repository": record["imageRepository"],
                "tag": record["imageTag"],
                "digest": record["imageDigest"],
            },
            "imagePullSecrets": [{"name": record["imagePullSecret"]}],
        },
        indent=2,
    ) + "\n"


def main():
    sha = os.environ["GITHUB_SHA"]
    digest = os.environ["IMAGE_DIGEST"]
    run_number = int(os.environ["GITHUB_RUN_NUMBER"])
    run_url = "https://github.com/codebefore/pilot.bff/actions/runs/" + os.environ["GITHUB_RUN_ID"]
    checkout = Path("gitops-checkout")
    for _ in range(5):
        latest = command(["gh", "api", "repos/codebefore/pilot.bff/git/ref/heads/main", "--jq", ".object.sha"])
        if latest != sha:
            print("Skipped superseded BFF commit; newest main must pass its own build.")
            return
        command(["git", "fetch", "origin", BRANCH], cwd=checkout)
        command(["git", "checkout", "-B", BRANCH, "origin/" + BRANCH], cwd=checkout)
        record_path = checkout / "releases/production/pilot-bff.json"
        record = release_record(json.loads(record_path.read_text()), sha, digest, run_number, run_url)
        if record is None:
            print("Release already recorded or superseded; no GitOps change.")
            return
        image_path = checkout / "environments/prod/releases/pilot-bff.image.yaml"
        image_path.parent.mkdir(parents=True, exist_ok=True)
        image_path.write_text(image_values(record))
        command(["git", "add", "environments/prod/releases/pilot-bff.image.yaml"], cwd=checkout)
        command(
            ["git", "-c", "user.name=pilot-release-bot", "-c", "user.email=pilot-release-bot@users.noreply.github.com",
             "commit", "--allow-empty", "-m", "Pin BFF image " + sha[:12]],
            cwd=checkout,
        )
        record["valuesRevision"] = command(["git", "rev-parse", "HEAD"], cwd=checkout)
        record_path.write_text(json.dumps(record, indent=2) + "\n")
        command(["git", "add", "releases/production/pilot-bff.json"], cwd=checkout)
        command(
            ["git", "-c", "user.name=pilot-release-bot", "-c", "user.email=pilot-release-bot@users.noreply.github.com",
             "commit", "-m", "Release BFF " + sha[:12] + " via GitHub Actions"],
            cwd=checkout,
        )
        if command(["gh", "api", "repos/codebefore/pilot.bff/git/ref/heads/main", "--jq", ".object.sha"]) != sha:
            print("Main advanced before publication; release skipped.")
            return
        result = subprocess.run(
            ["git", "push", "origin", "HEAD:refs/heads/" + BRANCH],
            cwd=checkout,
            capture_output=True,
            timeout=60,
        )
        if result.returncode == 0:
            print("GitOps release recorded. Production awaits manual Argo Sync.")
            return
        time.sleep(2)
    raise RuntimeError("GitOps push failed after bounded retries; production unchanged")


if __name__ == "__main__":
    main()
