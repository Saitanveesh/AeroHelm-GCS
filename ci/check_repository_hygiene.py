"""Read-only repository hygiene gate; does not delete source or touch vehicles."""
from __future__ import annotations

from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]

REQUIRED = (
    "MissionPlanner.sln",
    "MissionPlanner.csproj",
    "ExtLibs/wasm/wwwroot/Cesium/Cesium.js",
    "ExtLibs/wasm/wwwroot/Cesium/Workers/createVerticesFromHeightmap.js",
)
WEIGHT_SUFFIXES = (".onnx", ".ckpt", ".safetensors", ".pt", ".pth", ".tflite")


def audit_tracked_files() -> dict:
    output = subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT)
    tracked = {
        name.decode("utf-8", "surrogateescape")
        for name in output.split(b"\x00") if name
    }
    errors = []
    for name in REQUIRED:
        if name not in tracked or not (ROOT / name).is_file():
            errors.append("Required Mission Planner source/runtime asset absent: " + name)
    for path in sorted(tracked):
        lower = path.lower()
        if (path.startswith("ExtLibs/wasm/wwwroot/") and
                lower.endswith((".js.map", ".css.map"))):
            errors.append("Generated debugging source map tracked: " + path)
        if path.startswith("cloud/secrets/") or path in ("cloud/.env", ".venv-cloud"):
            errors.append("Cloud secret or local environment tracked: " + path)
        if lower.endswith(WEIGHT_SUFFIXES) and (
                "/test/" in lower or "/models/" in lower or lower.startswith("models/")):
            errors.append("Experimental model weight tracked: " + path)
    return {"files": len(tracked), "errors": errors}


def main() -> int:
    outcome = audit_tracked_files()
    print(f"Audited {outcome['files']} tracked paths; {len(outcome['errors'])} findings.")
    for error in outcome["errors"]:
        print("ERROR:", error)
    if not outcome["errors"]:
        print("PASS: core GCS assets kept; no generated WASM maps or cloud secrets.")
    return bool(outcome["errors"])


if __name__ == "__main__":
    raise SystemExit(main())
