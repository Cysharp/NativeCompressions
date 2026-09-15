"""Launch a built Catalyst .app and reject missing, stale, or failed results."""
import json
import pathlib
import subprocess
import sys
import time
import uuid

app = pathlib.Path(sys.argv[1]).resolve()
output = pathlib.Path(sys.argv[2]).resolve()
output.mkdir(parents=True, exist_ok=True)
run_id = str(uuid.uuid4())
result = output / f"result-{run_id}.json"
deadline = time.monotonic() + 90
with (output / "launch.log").open("w") as log:
    launcher = subprocess.Popen(
        ["open", "-n", "-W", str(app), "--args", str(result), run_id],
        stdout=log, stderr=subprocess.STDOUT,
    )
    try:
        while not result.exists():
            code = launcher.poll()
            if code is not None:
                raise RuntimeError(f"App launcher exited ({code}) without a result")
            if time.monotonic() >= deadline:
                raise TimeoutError("Catalyst app produced no result within 90 seconds")
            time.sleep(1)
        data = json.loads(result.read_text())
        if data.get("RunId") != run_id or data.get("Success") is not True or data.get("Architecture") != "Arm64":
            raise RuntimeError(f"Catalyst smoke test failed: {data}")
        launcher.wait(timeout=max(1, deadline - time.monotonic()))
        if launcher.returncode != 0:
            raise RuntimeError(f"App launcher failed: {launcher.returncode}")
        print(json.dumps(data, indent=2))
    finally:
        if launcher.poll() is None:
            # Target only this smoke executable in the dedicated CI job.
            subprocess.run(["pkill", "-x", "CatalystSmoke"], check=False)
            launcher.terminate()
            try:
                launcher.wait(timeout=5)
            except subprocess.TimeoutExpired:
                launcher.kill()
                launcher.wait()
