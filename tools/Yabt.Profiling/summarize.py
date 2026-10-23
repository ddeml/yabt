"""Aggregate completed profiling runs using only Python's standard library.

Run from any directory. Defaults resolve relative to this script:
    python tools/Yabt.Profiling/summarize.py

Raw reports are ignored local artifacts. This committed generator validates
and summarizes those reports for the committed measurement document.
Requires Python 3.10 or later; no third-party packages are needed.
"""

from __future__ import annotations

import argparse
import collections
import hashlib
import json
import math
import pathlib
import re
import statistics


REPORT_NAME = re.compile(
    r"^(?:flat|zip)-[0-9]+-(?:empty|unchanged|mixed)-"
    r"(?:retained|restore|backup)-[0-9]+[.]json$"
)
SCENARIO = re.compile(r"^(empty|unchanged|mixed)(?:[-_].*)?$")
ENVIRONMENT_KEYS = (
    "build", "machine", "runtime", "os", "architecture", "processors",
    "serverGc", "restoreMaximumConcurrency", "payloadMemoryThresholdBytes",
    "payloadTotalMemoryBudgetBytes", "maximumPrivateBytes",
    "sampleIntervalMilliseconds",
)
COUNT_KEYS = ("newCount", "changedCount", "extraCount", "unchangedCount")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def read_document(path: pathlib.Path) -> tuple[dict, str]:
    data = path.read_bytes()
    return json.loads(data.decode("utf-8-sig")), hashlib.sha256(data).hexdigest()


def stats(values: list[int | float]) -> list[int | float]:
    require(bool(values), "Cannot aggregate an empty metric.")
    require(all(math.isfinite(value) for value in values), "Non-finite metric.")
    return [statistics.median(values), min(values), max(values)]


def expected_outcome(report: dict, scenario: str) -> dict[str, int]:
    count = report["fixture"]["fileCount"]
    format_name = report["fixture"]["format"]
    if report["operation"] == "backup":
        require(scenario == "unchanged", "Only unchanged backup fixtures are supported.")
        logical_count = 2 if format_name == "zip" else count + 1
        return dict(zip(COUNT_KEYS, (0, 0, 0, logical_count)))
    if scenario == "empty":
        return dict(zip(COUNT_KEYS, (count + 1, 0, 0, 0)))
    if scenario == "unchanged":
        return dict(zip(COUNT_KEYS, (0, 0, 0, count + 1)))
    # Fixture mutations use index modulo ten, including non-round counts.
    changed = (count + 9) // 10
    missing = (count + 8) // 10
    extra = (count + 7) // 10
    return dict(zip(COUNT_KEYS, (missing, changed, extra, count + 1 - missing - changed)))


def load_run(path: pathlib.Path) -> dict:
    report, digest = read_document(path)
    label = path.name
    require(report.get("schemaVersion") == 1, f"{label}: unsupported schema")
    require(report.get("failure") is None, f"{label}: run failure")
    require(report["fixture"]["format"] in ("mirror", "zip"), f"{label}: unknown format")
    require(report["fixture"]["shape"] == "flat", f"{label}: expected flat fixture")
    require(report["fixture"]["fileCount"] > 0, f"{label}: invalid payload count")
    scenario_match = SCENARIO.fullmatch(report["destinationName"])
    require(scenario_match is not None, f"{label}: unrecognized destination scenario")
    scenario = scenario_match.group(1)
    require(report["operation"] in ("plan", "backup", "restore"), f"{label}: unknown operation")
    require(report["mode"] in ("normal", "retained"), f"{label}: unknown mode")
    require((report["mode"] == "retained") == (report["operation"] == "plan"),
            f"{label}: unexpected operation/mode combination")
    require(report["timingComparable"] == (report["mode"] == "normal"),
            f"{label}: unexpected timing comparability")
    checkpoint_delay = report.get("retainedCheckpointDelayMilliseconds", 0)
    if report["mode"] == "retained":
        require(checkpoint_delay == 100,
                f"{label}: retained report lacks the required 100 ms checkpoint delay")
    result = report["result"]
    require(isinstance(result, dict), f"{label}: no result")
    require(not result.get("failures") and not result.get("failedCount"),
            f"{label}: outcome failures")
    if report["mode"] == "normal":
        require(result.get("completed") is True, f"{label}: command not completed")
    expected = expected_outcome(report, scenario)
    require(all(result.get(key) == value for key, value in expected.items()),
            f"{label}: outcome count mismatch; expected {expected}, got {result}")
    if report["operation"] == "plan":
        count = report["fixture"]["fileCount"]
        format_name = report["fixture"]["format"]
        expected_plan = {
            "archiveObjectCount": 2 if format_name == "zip" else count + 1,
            "plannedFileCount": count + 1,
            "plannedDirectoryCount": 0,
            "emptyDirectoryCount": 0,
            "projectionCount": 1 if format_name == "zip" else count + 1,
            "filesToWriteCount": expected["newCount"] + expected["changedCount"],
            "historyMoveCount": expected["changedCount"] + expected["extraCount"],
        }
        require(all(result.get(key) == value for key, value in expected_plan.items()),
                f"{label}: plan count mismatch; expected {expected_plan}, got {result}")
    checkpoints = report["checkpoints"]
    phases = [checkpoint["phase"] for checkpoint in checkpoints]
    require(len(phases) == len(set(phases)), f"{label}: duplicate checkpoint phase")
    require(phases[0] == "baseline" and phases[-1] == "finished",
            f"{label}: missing baseline/finished checkpoint")
    if report["mode"] == "retained":
        require(phases == ["baseline", "prepared", "enumerated", "planned",
                           "destination-manifest", "reconciled", "released", "finished"],
                f"{label}: unexpected diagnostic checkpoint sequence")
    require(all(checkpoint["scratchLogicalBytes"] == 0 for checkpoint in checkpoints
                if checkpoint["phase"] in ("baseline", "released", "finished")),
            f"{label}: scratch storage not released")
    watchdog_path = path.with_name(path.stem + ".watchdog.json")
    watchdog, watchdog_digest = read_document(watchdog_path)
    require(watchdog.get("exitCode") == 0 and watchdog.get("failure") is None,
            f"{label}: watchdog failure")
    require(not watchdog.get("stderr"), f"{label}: watchdog recorded stderr")
    require(watchdog.get("schemaVersion") == 1, f"{label}: unsupported watchdog schema")
    return {
        "report": report,
        "watchdog": watchdog,
        "scenario": scenario,
        "source": {
            "report": path.name,
            "sha256": digest,
            "watchdog": watchdog_path.name,
            "watchdogSha256": watchdog_digest,
            "startedUtc": report["startedUtc"],
            "observedOtherProcessCpuSeconds": sum(
                process["cpuSeconds"] for process in watchdog["otherObservedProcessCpu"]
            ),
        },
    }


def aggregate_group(key: tuple, runs: list[dict]) -> dict:
    format_name, shape, count, scenario, operation, mode = key
    reports = [run["report"] for run in runs]
    outcomes = [
        {key: value for key, value in report["result"].items()
         if key.endswith("Count") or key == "completed"}
        for report in reports
    ]
    require(all(outcome == outcomes[0] for outcome in outcomes), f"{key}: outcomes vary")
    require(all([cp["phase"] for cp in report["checkpoints"]] ==
                [cp["phase"] for cp in reports[0]["checkpoints"]] for report in reports),
            f"{key}: checkpoint sequences vary")
    group = {
        "format": format_name,
        "shape": shape,
        "payloadFileCount": count,
        "scenario": scenario,
        "operation": operation,
        "mode": mode,
        "sampleCount": len(runs),
        "retainedCheckpointDelayMilliseconds": stats([
            report.get("retainedCheckpointDelayMilliseconds", 0) for report in reports
        ]),
        "maximumSeconds": stats([report["maximumSeconds"] for report in reports]),
        "sources": [run["source"] for run in runs],
        "validatedSharedOutcome": outcomes[0],
    }
    if mode == "normal":
        group["elapsedMilliseconds"] = stats([report["elapsedMilliseconds"] for report in reports])
    group["allocatedBytesDelta"] = stats([
        report["checkpoints"][-1]["totalAllocatedBytes"] - report["checkpoints"][0]["totalAllocatedBytes"]
        for report in reports
    ])
    group["processCpuMillisecondsDelta"] = stats([
        report["checkpoints"][-1]["processCpuMilliseconds"] - report["checkpoints"][0]["processCpuMilliseconds"]
        for report in reports
    ])
    for field in ("sampledPeakPrivateBytes", "sampledPeakWorkingSetBytes", "sampledPeakManagedBytes"):
        group[field] = stats([report[field] for report in reports])
    group["observedOtherProcessCpuSeconds"] = stats([
        run["source"]["observedOtherProcessCpuSeconds"] for run in runs
    ])
    process_totals = collections.defaultdict(float)
    for run in runs:
        for process in run["watchdog"]["otherObservedProcessCpu"]:
            process_totals[process["name"]] += process["cpuSeconds"]
    group["observedOtherProcessCpuSecondsAcrossRunsByName"] = dict(sorted(process_totals.items()))
    group["checkpoints"] = []
    for index, reference in enumerate(reports[0]["checkpoints"]):
        checkpoints = [report["checkpoints"][index] for report in reports]
        checkpoint = {
            "phase": reference["phase"],
            "scratchLogicalBytes": stats([cp["scratchLogicalBytes"] for cp in checkpoints]),
        }
        if mode == "retained":
            checkpoint["managedBytes"] = stats([cp["managedBytes"] for cp in checkpoints])
            checkpoint["managedBytesAboveBaseline"] = stats([
                cp["managedBytes"] - report["checkpoints"][0]["managedBytes"]
                for cp, report in zip(checkpoints, reports)
            ])
            checkpoint["fragmentedBytesAtLastCollection"] = stats([
                cp["fragmentedBytesAtLastCollection"] for cp in checkpoints
            ])
            checkpoint["gen2CollectionsAboveBaseline"] = stats([
                cp["gen2Collections"] - report["checkpoints"][0]["gen2Collections"]
                for cp, report in zip(checkpoints, reports)
            ])
            if index:
                checkpoint["allocatedBytesSincePreviousCheckpoint"] = stats([
                    cp["totalAllocatedBytes"] - report["checkpoints"][index - 1]["totalAllocatedBytes"]
                    for cp, report in zip(checkpoints, reports)
                ])
        group["checkpoints"].append(checkpoint)
    return group


def pretty_json(value, level: int = 0) -> str:
    """Keep numeric statistic triples inline without hiding object structure."""
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


def main() -> None:
    script = pathlib.Path(__file__).resolve()
    repository = script.parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=pathlib.Path, default=repository / "artifacts" / "plan-memory" / "results")
    parser.add_argument("--output", type=pathlib.Path, default=repository / "docs" / "plan-memory-results.json")
    args = parser.parse_args()
    paths = sorted(path for path in args.input.glob("*.json") if REPORT_NAME.fullmatch(path.name))
    require(bool(paths), "No completed repeated measurement reports found.")
    runs = [load_run(path) for path in paths]
    reports = [run["report"] for run in runs]
    environment = {key: reports[0][key] for key in ENVIRONMENT_KEYS}
    require(all(all(report[key] == value for key, value in environment.items()) for report in reports),
            "Reports from different builds, environments, or runtime settings must not be combined.")
    groups = collections.defaultdict(list)
    for run in runs:
        report = run["report"]
        fixture = report["fixture"]
        groups[(fixture["format"], fixture["shape"], fixture["fileCount"], run["scenario"],
                report["operation"], report["mode"])].append(run)
    summary = {
        "schemaVersion": 1,
        "documentType": "yabt.planMemoryMeasurements",
        "sourceDirectory": args.input.resolve().relative_to(repository).as_posix()
        if args.input.resolve().is_relative_to(repository) else str(args.input.resolve()),
        "generator": "tools/Yabt.Profiling/summarize.py",
        "generatorSha256": hashlib.sha256(script.read_bytes()).hexdigest(),
        "statisticsOrder": ["median", "minimum", "maximum"],
        "totalRunCount": len(runs),
        "allRunsSucceeded": True,
        "allExpectedOutcomeCountsValidated": True,
        "allReleasedScratchLengthsZero": True,
        "firstStartedUtc": min(report["startedUtc"] for report in reports),
        "lastStartedUtc": max(report["startedUtc"] for report in reports),
        "productionCommit": environment["build"].partition("+")[2] or None,
        "environment": environment,
        "interpretation": [
            "Statistic arrays contain median, minimum, maximum in the named unit.",
            "Only normal public-command runs have comparable wall-clock timings.",
            "Retained diagnostics include forced-GC observer CPU and allocations.",
            "Retained planning probes require a 100 ms asynchronous delay before collection; this is practical quiescence, not a strict guarantee.",
            "Baseline-adjusted managed bytes are computed within each run before aggregation.",
            "Scratch lengths are checkpoint file lengths, not I/O volume or peak physical allocation.",
            "Sampled peaks may miss short spikes; private memory includes managed and native memory.",
            "Other-process CPU is endpoint-observed guest CPU, not proof of host or guest idleness.",
            "Filename selection excludes heap captures, smoke, preparation, and checkpoint sidecars.",
        ],
        "groups": [aggregate_group(key, grouped_runs) for key, grouped_runs in sorted(groups.items())],
    }
    args.output.write_bytes((pretty_json(summary) + "\n").replace("\n", "\r\n").encode("utf-8"))
    print(f"Validated {len(runs)} runs in {len(groups)} groups; wrote {args.output}")


if __name__ == "__main__":
    main()
