#!/usr/bin/env python3
import importlib.util
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("promotion", Path(__file__).with_name("promote-gitops-release.py"))
promotion = importlib.util.module_from_spec(spec)
spec.loader.exec_module(promotion)


class ReleaseTests(unittest.TestCase):
    def test_older_run_cannot_replace_newer_release(self):
        self.assertIsNone(promotion.release_record({"workflowRunNumber": 20}, "a" * 40, "sha256:" + "b" * 64, 19, "fixture"))

    def test_invalid_revision_or_digest_is_rejected(self):
        for sha, digest in [("main", "sha256:" + "b" * 64), ("a" * 40, "latest")]:
            with self.assertRaises(RuntimeError):
                promotion.release_record({}, sha, digest, 1, "fixture")

    def test_duplicate_digest_is_noop(self):
        self.assertIsNone(promotion.release_record(
            {"bffRevision": "a" * 40, "imageDigest": "sha256:" + "b" * 64},
            "a" * 40,
            "sha256:" + "b" * 64,
            2,
            "fixture",
        ))


class ImmutableValuesIntegrationTests(unittest.TestCase):
    def test_promoter_records_the_commit_containing_the_exact_image(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            origin = root / "origin.git"
            seed = root / "seed"

            def git(*args, cwd=None):
                return subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, check=True).stdout.strip()

            git("init", "--bare", str(origin))
            git("init", str(seed))
            git("checkout", "-b", promotion.BRANCH, cwd=seed)
            record = seed / "releases/production/pilot-bff.json"
            record.parent.mkdir(parents=True)
            record.write_text(json.dumps({"workflowRunNumber": 0, "valuesRevision": "c" * 40}))
            values = seed / "environments/prod/values/pilot-bff.values.yaml"
            values.parent.mkdir(parents=True)
            values.write_text("env:\n  marker: preserved\n")
            git("add", ".", cwd=seed)
            git("-c", "user.name=fixture", "-c", "user.email=fixture@example.test", "commit", "-m", "baseline", cwd=seed)
            git("remote", "add", "origin", str(origin), cwd=seed)
            git("push", "origin", promotion.BRANCH, cwd=seed)
            git("clone", "--branch", promotion.BRANCH, str(origin), str(root / "gitops-checkout"))
            original = promotion.command

            def command(args, **kwargs):
                return "a" * 40 if args[0] == "gh" else original(args, **kwargs)

            previous_directory = os.getcwd()
            try:
                os.chdir(root)
                with patch.object(promotion, "command", side_effect=command), patch.dict(os.environ, {
                    "GITHUB_SHA": "a" * 40,
                    "IMAGE_DIGEST": "sha256:" + "b" * 64,
                    "GITHUB_RUN_NUMBER": "1",
                    "GITHUB_RUN_ID": "12",
                }):
                    promotion.main()
            finally:
                os.chdir(previous_directory)
            checkout = root / "gitops-checkout"
            release = json.loads((checkout / "releases/production/pilot-bff.json").read_text())
            pinned = json.loads(git(
                "show",
                release["valuesRevision"] + ":environments/prod/releases/pilot-bff.image.yaml",
                cwd=checkout,
            ))
            self.assertEqual("ghcr.io/codebefore/pilot-bff", pinned["image"]["repository"])
            self.assertEqual(release["imageDigest"], pinned["image"]["digest"])
            self.assertEqual(git("rev-parse", "HEAD^", cwd=checkout), release["valuesRevision"])
            self.assertIn("marker: preserved", git(
                "show",
                release["valuesRevision"] + ":environments/prod/values/pilot-bff.values.yaml",
                cwd=checkout,
            ))


if __name__ == "__main__":
    unittest.main()
