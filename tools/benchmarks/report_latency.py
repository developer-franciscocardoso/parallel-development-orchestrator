"""Measure the authenticated plan report on an isolated synthetic journal."""
import argparse
import json
import os
from pathlib import Path
import secrets
import socket
import sqlite3
import statistics
import subprocess
import tempfile
import time
import urllib.request


def measure(executable, rows, iterations, summary):
    with tempfile.TemporaryDirectory(prefix="pda-report-") as temp:
        root = Path(temp)
        env = dict(os.environ, PDA_HOME=str(root / "state"),
                   PDA_DASHBOARD_TOKEN=secrets.token_hex(32))
        repo = root / "repo"
        repo.mkdir()

        def run(*args):
            subprocess.run(args, cwd=repo, env=env, check=True,
                           stdout=subprocess.DEVNULL, timeout=90)

        run("git", "init", "-q", "-b", "main")
        (repo / "sample.txt").write_text("fixture", encoding="utf-8")
        run("git", "add", "sample.txt")
        run("git", "-c", "user.name=Fixture", "-c", "user.email=pda@invalid",
            "commit", "-qm", "baseline")
        run(executable, "project", "add", "sample", str(repo))
        checks = root / "checks.json"
        checks.write_text('[{"executable":"test","arguments":[]}]')
        run(executable, "plan", "create", "sample", "trial", "--budget", "20",
            "--objective", "fixture", "--owned", "sample.txt",
            "--checks", str(checks))
        database = next((root / "state").glob("*.db"))
        with sqlite3.connect(database) as db:
            db.executemany("""
                INSERT INTO journal(plan,kind,payload,timestamp)
                VALUES('trial','BenchmarkFixture',?,
                       '2026-01-01T00:00:00.0000000+00:00')
            """, [(json.dumps({"fixture": "x" * 512}),)] * rows)
        db.close()
        with socket.socket() as local:
            local.bind(("127.0.0.1", 0))
            port = local.getsockname()[1]
        process = subprocess.Popen([executable, "dashboard", "--port",
                                    str(port)], env=env, cwd=root)
        try:
            request = urllib.request.Request(
                f"http://127.0.0.1:{port}/api/plans/trial" + ("/summary" if summary else ""),
                headers={"Authorization": "Bearer " + env["PDA_DASHBOARD_TOKEN"]})
            for _ in range(150):
                try:
                    with urllib.request.urlopen(request, timeout=10) as result:
                        result.read()
                    break
                except OSError:
                    if process.poll() is not None:
                        raise RuntimeError("Dashboard exited before readiness")
                    time.sleep(0.2)
            samples = []
            for _ in range(iterations):
                started = time.perf_counter()
                with urllib.request.urlopen(request, timeout=30) as result:
                    response = result.read()
                samples.append(round((time.perf_counter() - started) * 1000, 2))
            print(json.dumps({"rows": rows, "samplesMs": samples,
                              "medianMs": statistics.median(samples),
                              "responseBytes": len(response),
                              "journalRowsReturned": len(json.loads(response)["history"])},
                             indent=2))
        finally:
            process.terminate()
            process.wait(timeout=15)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=lambda p: str(Path(p).resolve()))
    parser.add_argument("--rows", type=int, default=5000)
    parser.add_argument("--iterations", type=int, default=15)
    parser.add_argument("--summary", action="store_true")
    args = parser.parse_args()
    if not 1 <= args.rows <= 100000 or not 1 <= args.iterations <= 1000:
        parser.error("rows must be 1..100000 and iterations 1..1000")
    measure(args.executable, args.rows, args.iterations, args.summary)
