"""Validate and aggregate resume profiling reports with the standard library.

Defaults resolve from this file, so the working directory does not matter.
Source reports remain ignored artifacts; the compact summary records their hashes.
Requires Python 3.10 or later.
"""

from __future__ import annotations

import argparse
import collections
import datetime
import hashlib
import json
import math
import pathlib
import statistics


ENVIRONMENT_KEYS = (
    "machine", "runtime", "os", "architecture", "processors", "serverGc",
    "build", "warmup", "baselineCollection", "sampleIntervalMilliseconds",
    "restoreMaximumConcurrency", "progressFlushUnitCount",
    "progressFlushIntervalSeconds", "payloadMemoryThresholdBytes",
    "payloadTotalMemoryBudgetBytes",
)
PHASE_METRICS = (
    "elapsedMilliseconds", "firstPayloadCommitMilliseconds",
    "firstNewPayloadCommitMilliseconds", "payloadCommitCount",
    "previouslyCommittedCount", "recommittedPayloadCount", "allocatedBytes",
    "processCpuMilliseconds", "sampledPeakManagedBytes", "sampledPeakPrivateBytes",
    "sampledPeakWorkingSetBytes", "remainingProgressDatabases",
)
CALL_METRICS = (
    "providerObjectReadCalls", "providerFolderListCalls", "sourceObjectReadCalls",
    "archiveObjectReadCalls", "destinationObjectReadCalls", "zipEntryReadCalls",
    "zipSourceReadCalls", "progressUnavailableWarnings",
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def read_document(path: pathlib.Path) -> tuple[dict, str]:
    data = path.read_bytes()
    return json.loads(data.decode("utf-8-sig")), hashlib.sha256(data).hexdigest()


def statistics_or_null(values: list[int | float | None]) -> list[int | float] | None:
    require(bool(values), "Cannot aggregate an empty metric.")
    if all(value is None for value in values):
        return None
    require(all(value is not None and math.isfinite(value) and value >= 0 for value in values),
            "Missing, negative, or non-finite metric.")
    return [statistics.median(values), min(values), max(values)]


def validate_phase(phase: dict, scenario: dict, name: str, interrupted: bool) -> None:
    require(phase.get("schemaVersion") == 1, f"{name}: unsupported phase schema")
    require(phase.get("failure") is None and phase.get("timingComparable") is True,
            f"{name}: failed or non-comparable phase")
    require(phase.get("interrupted") is interrupted and phase.get("completed") is (not interrupted),
            f"{name}: wrong completion/interruption state")
    require(phase.get("operation") == scenario["operation"] and
            phase.get("format") == scenario["format"] and
            phase.get("progressEnabled") is scenario["progressEnabled"],
            f"{name}: scenario differs from worker settings")
    require(phase.get("recommittedPayloadCount") == 0,
            f"{name}: unexpected rewrite of previously committed payloads")
    require(phase.get("calls", {}).get("progressUnavailableWarnings") == 0,
            f"{name}: requested progress storage was unavailable")
    expected_databases = 1 if interrupted and scenario["progressEnabled"] else 0
    require(phase.get("remainingProgressDatabases") == expected_databases,
            f"{name}: unexpected retained progress databases")
    require(all(key in phase for key in ENVIRONMENT_KEYS), f"{name}: missing environment/method fields")
    require(phase["warmup"] is False and phase["baselineCollection"] is True,
            f"{name}: unexpected warmup/collection method")
    require([item["phase"] for item in phase["checkpoints"]] == ["baseline", "finished"],
            f"{name}: unexpected sampler checkpoints")
    if phase["payloadCommitCount"]:
        require(phase["firstPayloadCommitMilliseconds"] is not None,
                f"{name}: missing first payload latency")
        require(phase["firstNewPayloadCommitMilliseconds"] is not None,
                f"{name}: missing first new payload latency")
    else:
        require(phase["firstPayloadCommitMilliseconds"] is None and
                phase["firstNewPayloadCommitMilliseconds"] is None,
                f"{name}: latency without a payload commit")
    if not interrupted:
        result = phase.get("result") or {}
        require(result.get("completed") is True and not result.get("failedCount") and
                not result.get("failures") and result.get("extraCount") == 0,
                f"{name}: unexpected operation result")


def load_run(path: pathlib.Path) -> dict | None:
    report, digest = read_document(path)
    if not all(key in report for key in ("scenario", "first", "resumed")):
        return None
    label = path.name
    require(report.get("schemaVersion") == 1, f"{label}: unsupported report schema")
    scenario = report["scenario"]
    count = scenario["fixture"]["fileCount"]
    format_name = scenario["format"]
    operation = scenario["operation"]
    abort_after = scenario["abortAfterPayloadCommits"]
    require(scenario["fixture"]["shape"] == "grouped" and 0 < count <= 100000,
            f"{label}: unsupported fixture shape/count")
    require(format_name in ("mirror", "child-zip", "root-zip"), f"{label}: unknown format")
    require(operation in ("backup", "restore"), f"{label}: unknown operation")
    expected = count
    if operation == "backup" and format_name == "child-zip":
        expected = (count + 99) // 100
    elif operation == "backup" and format_name == "root-zip":
        expected = 1
    require(0 <= abort_after <= expected, f"{label}: impossible interruption count")
    first = report["first"]
    validate_phase(first, scenario, label + ":first", abort_after > 0)
    require(first["previouslyCommittedCount"] == 0, f"{label}: first attempt had prior commits")
    require(first["payloadCommitCount"] == (abort_after or expected),
            f"{label}: first attempt payload count mismatch")
    resumed = report["resumed"]
    if abort_after:
        require(isinstance(resumed, dict), f"{label}: missing resumed attempt")
        validate_phase(resumed, scenario, label + ":resume", False)
        require(resumed["processId"] != first["processId"], f"{label}: attempts shared a process")
        require(resumed["previouslyCommittedCount"] == abort_after and
                resumed["payloadCommitCount"] == expected - abort_after,
                f"{label}: resumed payload coverage mismatch")
        require(all(first[key] == resumed[key] for key in ENVIRONMENT_KEYS),
                f"{label}: attempts used different environments/settings")
    else:
        require(resumed is None, f"{label}: uninterrupted scenario has a second attempt")

    watchdog_path = path.with_suffix(".watchdog.json")
    watchdog = None
    if watchdog_path.exists():
        record, watchdog_hash = read_document(watchdog_path)
        require(record.get("exitCode") == 0 and record.get("failure") is None,
                f"{label}: watchdog failure")
        watchdog = {
            "name": watchdog_path.name,
            "sha256": watchdog_hash,
            "otherObservedProcessCpu": record.get("otherObservedProcessCpu", []),
        }
    return {"name": label, "sha256": digest, "report": report, "watchdog": watchdog}


def aggregate_phase(phases: list[dict]) -> dict:
    result = {key: statistics_or_null([phase[key] for phase in phases]) for key in PHASE_METRICS}
    result["calls"] = {
        key: statistics_or_null([phase["calls"][key] for phase in phases]) for key in CALL_METRICS
    }
    before = [phase["callsBeforeFirstNewCommit"] for phase in phases]
    if all(item is None for item in before):
        result["callsBeforeFirstNewCommit"] = None
    else:
        require(all(item is not None for item in before), "Inconsistent first-new-commit presence.")
        result["callsBeforeFirstNewCommit"] = {
            key: statistics_or_null([item[key] for item in before]) for key in CALL_METRICS
        }
    return result


def pretty_json(value, level: int = 0) -> str:
    """Keep numeric statistic triples inline, as in the plan-memory summary."""
    indent = "  " * level
    child_indent = "  " * (level + 1)
    if isinstance(value, dict) and value:
        return "{\n" + ",\n".join(
            child_indent + json.dumps(key) + ": " + pretty_json(item, level + 1)
            for key, item in value.items()
        ) + "\n" + indent + "}"
    if isinstance(value, list) and value and not all(isinstance(item, (int, float)) for item in value):
        return "[\n" + ",\n".join(
            child_indent + pretty_json(item, level + 1) for item in value
        ) + "\n" + indent + "]"
    return json.dumps(value, ensure_ascii=False, allow_nan=False)


def summarize(runs: list[dict]) -> dict:
    require(bool(runs), "No completed main resume profiling reports found.")
    environment = {key: runs[0]["report"]["first"][key] for key in ENVIRONMENT_KEYS}
    grouped = collections.defaultdict(list)
    starts, ends = [], []
    for run in runs:
        report = run["report"]
        scenario = report["scenario"]
        require(all(report["first"][key] == value for key, value in environment.items()),
                f"{run['name']}: build/environment differs from the other runs")
        key = (scenario["format"], scenario["fixture"]["fileCount"], scenario["operation"],
               scenario["abortAfterPayloadCommits"], scenario["progressEnabled"])
        grouped[key].append(run)
        for phase in (report["first"], report["resumed"]):
            if phase is not None:
                started = datetime.datetime.fromisoformat(phase["startedUtc"]).astimezone(datetime.timezone.utc)
                starts.append(started)
                ends.append(started + datetime.timedelta(milliseconds=phase["elapsedMilliseconds"]))

    groups = []
    for key, samples in sorted(grouped.items()):
        format_name, count, operation, abort_after, enabled = key
        groups.append({
            "format": format_name, "fileCount": count, "operation": operation,
            "mode": "resume" if abort_after else "normal",
            "abortAfterPayloadCommits": abort_after, "progressEnabled": enabled,
            "samples": len(samples),
            "first": aggregate_phase([sample["report"]["first"] for sample in samples]),
            "resumed": aggregate_phase([sample["report"]["resumed"] for sample in samples]) if abort_after else None,
            "sources": [{"name": sample["name"], "sha256": sample["sha256"],
                         "watchdog": sample["watchdog"]} for sample in samples],
        })
    return {
        "schemaVersion": 1,
        "metricOrder": ["median", "minimum", "maximum"],
        "environment": environment,
        "measurementWindowUtc": {"first": min(starts).isoformat(), "last": max(ends).isoformat()},
        "acceptedRuns": len(runs),
        "acceptedWorkerAttempts": sum(1 + (run["report"]["resumed"] is not None) for run in runs),
        "watchdogReports": sum(run["watchdog"] is not None for run in runs),
        "method": {
            "interruption": "Cooperative cancellation after distinct verified payload commits; safe progress flushes on disposal.",
            "isolation": "Fresh process per attempt; source creation and restore archive setup are outside measured attempts.",
            "cache": "Filesystem caches are not flushed; initial JIT is measured, process startup is excluded.",
            "counters": "Structured provider invocation events, including control metadata and failed calls; not bytes or all native IO.",
            "memory": "Sampled process peaks and cumulative allocations; observer path sets/logs included; no total-memory bound claim.",
        },
        "groups": groups,
    }


def main() -> None:
    repository = pathlib.Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=pathlib.Path, default=repository / "artifacts/incremental-resume/results")
    parser.add_argument("--output", type=pathlib.Path, default=repository / "docs/incremental-resume-results.json")
    args = parser.parse_args()
    runs = []
    for path in sorted(args.input.glob("*.json")):
        run = load_run(path)
        if run is not None:
            runs.append(run)
    summary = summarize(runs)
    summary["generator"] = "tools/Yabt.Profiling/summarize-resume.py"
    summary["generatorSha256"] = hashlib.sha256(pathlib.Path(__file__).read_bytes()).hexdigest()
    with args.output.open("w", encoding="utf-8", newline="\r\n") as stream:
        stream.write(pretty_json(summary) + "\n")
    print(f"Accepted {summary['acceptedRuns']} scenarios / {summary['acceptedWorkerAttempts']} worker attempts.")
    print(args.output)


if __name__ == "__main__":
    main()
