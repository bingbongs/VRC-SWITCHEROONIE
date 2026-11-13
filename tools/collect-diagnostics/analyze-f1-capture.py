"""Read-only comparison of a background pose sampler and controlled-scene log.

Example:
  python tools/collect-diagnostics/analyze-f1-capture.py --epoch 1384081817962 \
    --output reports/f1-failed-capture-analysis.json --human-result "Failed"
Requires NumPy for bounded-memory exhaustive pairwise motion diameters.
"""

import argparse
import collections
import datetime
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import statistics
import subprocess

os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
os.environ.setdefault("OMP_NUM_THREADS", "1")
import numpy as np

spec = importlib.util.spec_from_file_location("capture_math", Path(__file__).with_name("analyze-live-capture.py"))
cm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cm)


def load_capture(path, event):
    raw = path.read_bytes()
    end = raw.rfind(b"\n") + 1
    snapshot = raw[:end]
    records = [json.loads(line) for line in snapshot.decode("utf-8-sig").splitlines() if line.strip()]
    return {
        "snapshotBytes": len(snapshot),
        "snapshotSha256": hashlib.sha256(snapshot).hexdigest(),
        "incompleteTrailingBytesExcluded": len(raw) - end,
        "lastWriteUtc": datetime.datetime.fromtimestamp(path.stat().st_mtime, datetime.timezone.utc).isoformat(),
        "rows": [row for row in records if row.get("event") == event],
        "meta": [row for row in records if row.get("event") in ("header", "start", "end")],
    }


def angular_diameter(values):
    if not values:
        return None
    if len(values) < 2:
        return 0.0
    array = np.array(values, dtype=np.float64)
    array /= np.linalg.norm(array, axis=1)[:, None]
    best, pair = 2.0, (0, 0)
    for start in range(0, len(array), 256):
        dots = np.abs(array[start:start + 256] @ array.T)
        row, column = np.unravel_index(np.argmin(dots), dots.shape)
        value = dots[row, column]
        if value < best:
            best, pair = float(value), (start + row, column)
    return cm.quaternion_angle_degrees(values[pair[0]], values[pair[1]])


def position_diameter(values):
    if not values:
        return None
    array = np.array(values, dtype=np.float64)
    best = 0.0
    for start in range(0, len(array), 128):
        delta = array[start:start + 128, None, :] - array[None, :, :]
        best = max(best, float(np.max(np.einsum("ijk,ijk->ij", delta, delta))))
    return math.sqrt(best)


def canonical(row, scene, first_sampler_qpc, frequency):
    driver = row["driver"] if scene else row["driverCapture"]
    matrix = row["routedHeadStandingMatrix"] if scene else row["clientHead"]["standingMatrix34"]
    return {
        "index": row["frame"] if scene else row["sample"],
        "t": row["elapsedSeconds"] if scene else (row["qpc"] - first_sampler_qpc) / frequency,
        "epoch": driver["epoch"] if scene else driver["ackEpoch"],
        "mode": driver["mode"] if scene else driver["actualMode"],
        "anchorEpoch": driver.get("anchorEpoch"),
        "syntheticValid": driver.get("routedSyntheticHeadAvailable" if scene else "routedSyntheticHeadValid"),
        "syntheticEpoch": driver.get("syntheticHeadEpoch"),
        "syntheticQpc": driver.get("syntheticHeadQpc"),
        "syntheticAgeMs": driver.get("syntheticHeadAgeMilliseconds" if scene else "syntheticHeadAgeMs"),
        "syntheticP": driver.get("routedSyntheticWorldPosition"),
        "syntheticQ": driver.get("routedSyntheticWorldQuaternionWxyz"),
        "sourceValid": driver["capturedPhysicalHeadAvailable"] if scene else driver["capturedHeadValid"],
        "headAgeMs": driver["headAgeMilliseconds"] if scene else driver["headAgeMs"],
        "statusAgeMs": driver["statusAgeMilliseconds"] if scene else driver["statusAgeMs"],
        "statusFresh": driver["statusAlive"] if scene else driver["statusFresh"],
        "error": driver["error"],
        "physicalSamples": driver["physicalSamples"],
        "routedSamples": driver["routedSamples"],
        "sourceP": driver["capturedPhysicalHeadWorldPosition"] if scene else driver["capturedWorldPosition"],
        "sourceQ": driver["capturedPhysicalHeadWorldQuaternionWxyz"] if scene else driver["capturedWorldQuaternionWxyz"],
        "matrix": matrix,
        "routedP": [matrix[3], matrix[7], matrix[11]],
        "routedQ": cm.matrix_quaternion(matrix),
        "headValid": row["headValid"] if scene else row["clientHead"]["poseValid"],
        "connected": row["headConnected"] if scene else row["clientHead"]["connected"],
        "pid": row["processId"] if scene else row["sceneProcessId"],
    }


def motion(rows):
    valid = [row for row in rows if row["sourceValid"] and row["sourceP"] is not None and row["sourceQ"] is not None]
    result = {
        "samples": len(rows),
        "sourceValidSamples": len(valid),
        "sourceAngularDiameterDegrees": angular_diameter([row["sourceQ"] for row in valid]),
        "sourcePositionDiameterMeters": position_diameter([row["sourceP"] for row in valid]),
        "sourcePositionAxisRangesMeters": cm.axis_ranges([row["sourceP"] for row in valid]) if valid else None,
        "routedAngularDiameterDegrees": angular_diameter([row["routedQ"] for row in rows]),
        "routedPositionDiameterMeters": position_diameter([row["routedP"] for row in rows]),
        "routedPositionAxisRangesMeters": cm.axis_ranges([row["routedP"] for row in rows]) if rows else None,
    }
    synthetic = [row for row in rows if row["syntheticValid"] and row["syntheticP"] is not None and row["syntheticQ"] is not None]
    if any(row["syntheticValid"] is not None for row in rows):
        result.update(
            syntheticWorldValidSamples=len(synthetic),
            syntheticWorldAngularDiameterDegrees=angular_diameter([row["syntheticQ"] for row in synthetic]),
            syntheticWorldPositionDiameterMeters=position_diameter([row["syntheticP"] for row in synthetic]),
            syntheticWorldPositionAxisRangesMeters=cm.axis_ranges([row["syntheticP"] for row in synthetic]) if synthetic else None,
            uniqueSerializedSyntheticWorldPoses=len({tuple(row["syntheticP"]) + tuple(row["syntheticQ"]) for row in synthetic}),
        )
    return result


def integrity(rows):
    gaps = [b["t"] - a["t"] for a, b in zip(rows, rows[1:])]
    ages = [row["headAgeMs"] for row in rows]
    anchors = [row["anchorEpoch"] for row in rows if row["anchorEpoch"] is not None]
    result = {
        "samples": len(rows), "firstIndex": rows[0]["index"], "lastIndex": rows[-1]["index"],
        "firstTimeSeconds": rows[0]["t"], "lastTimeSeconds": rows[-1]["t"],
        "spanSeconds": rows[-1]["t"] - rows[0]["t"],
        "maxSampleGapSeconds": max(gaps, default=0),
        "medianSampleGapSeconds": statistics.median(gaps) if gaps else None,
        "modeCounts": dict(collections.Counter(str(row["mode"]) for row in rows)),
        "epochCounts": dict(collections.Counter(str(row["epoch"]) for row in rows)),
        "optionalAnchorEpochCounts": dict(collections.Counter(str(value) for value in anchors)),
        "sceneProcessIds": sorted({row["pid"] for row in rows}),
        "headAgeMsMin": min(ages), "headAgeMsMax": max(ages),
        "negativeHeadAgeRecordedSamples": sum(value < 0 for value in ages),
        "headAgeOver200MsSamples": sum(value > 200 for value in ages),
        "sourceValidSamples": sum(row["sourceValid"] for row in rows),
        "invalidClientHeadSamples": sum(not row["headValid"] for row in rows),
        "disconnectedClientHeadSamples": sum(not row["connected"] for row in rows),
        "nonfreshStatusSamples": sum(not row["statusFresh"] for row in rows),
        "statusAgeMsMax": max(row["statusAgeMs"] for row in rows),
        "nonzeroDriverErrorSamples": sum(row["error"] != 0 for row in rows),
        "physicalSamplesFirst": rows[0]["physicalSamples"], "physicalSamplesLast": rows[-1]["physicalSamples"],
        "routedSamplesFirst": rows[0]["routedSamples"], "routedSamplesLast": rows[-1]["routedSamples"],
    }
    if anchors:
        result["recordedAnchorEpochChangeCount"] = sum(b != a for a, b in zip(anchors, anchors[1:]))
    synthetic = [row for row in rows if row["syntheticValid"] is not None]
    if synthetic:
        ages_s = [row["syntheticAgeMs"] for row in synthetic if row["syntheticAgeMs"] is not None]
        result.update(
            syntheticFieldsRecordedSamples=len(synthetic),
            syntheticValidSamples=sum(bool(row["syntheticValid"]) for row in synthetic),
            syntheticEpochCounts=dict(collections.Counter(str(row["syntheticEpoch"]) for row in synthetic)),
            syntheticEpochMatchesAcknowledgedEpochSamples=sum(row["syntheticEpoch"] == row["epoch"] for row in synthetic),
            syntheticHeadAgeMsMin=min(ages_s) if ages_s else None,
            syntheticHeadAgeMsMax=max(ages_s) if ages_s else None,
            syntheticNegativeAgeRecordedSamples=sum(value < 0 for value in ages_s),
            syntheticQpcStrictlyAdvances=all(b["syntheticQpc"] > a["syntheticQpc"] for a, b in zip(synthetic, synthetic[1:])),
        )
    return result


def plateaus(rows, epoch, include_details=False):
    groups, changes, start = [], [], 0
    for index in range(1, len(rows) + 1):
        if index == len(rows) or rows[index]["matrix"] != rows[index - 1]["matrix"]:
            group = rows[start:index]
            valid = [row for row in group if row["sourceValid"]]
            groups.append({
                "firstIndex": group[0]["index"], "lastIndex": group[-1]["index"],
                "firstTimeSeconds": group[0]["t"], "lastTimeSeconds": group[-1]["t"],
                "durationSeconds": group[-1]["t"] - group[0]["t"], "samples": len(group),
                "sourceAngularDiameterDegrees": angular_diameter([row["sourceQ"] for row in valid]),
                "sourcePositionDiameterMeters": position_diameter([row["sourceP"] for row in valid]),
            })
            start = index
    for previous, current in zip(rows, rows[1:]):
        if previous["matrix"] == current["matrix"]:
            continue
        changes.append({
            "index": current["index"], "timeSeconds": current["t"],
            "sameRecordedModeAndEpoch": previous["mode"] == current["mode"] == 1 and previous["epoch"] == current["epoch"] == epoch,
            "routedAngularJumpDegrees": cm.quaternion_angle_degrees(previous["routedQ"], current["routedQ"]),
            "routedPositionJumpMeters": math.dist(previous["routedP"], current["routedP"]),
            "headAgeMs": current["headAgeMs"], "statusAgeMs": current["statusAgeMs"],
        })
    result = {
        "plateauCount": len(groups), "routedMatrixChangeCount": len(changes),
        "changesWithSameRecordedDesktopModeAndEpoch": sum(change["sameRecordedModeAndEpoch"] for change in changes),
        "longestPlateau": max(groups, key=lambda group: group["durationSeconds"]),
        "largestAngularJumps": sorted(changes, key=lambda change: change["routedAngularJumpDegrees"], reverse=True)[:5],
        "largestPositionJumps": sorted(changes, key=lambda change: change["routedPositionJumpMeters"], reverse=True)[:5],
    }
    if include_details:
        result["allChangeTimesSeconds"] = [change["timeSeconds"] for change in changes]
        result["plateaus"] = groups
    return result


def process_snapshot():
    command = "Get-Process -Name switcheroonie-test-scene,Switcheroonie.Broker,Switcheroonie.UI,vrserver,vrcompositor,VRChat,VirtualDesktop.Streamer -ErrorAction SilentlyContinue | Select-Object ProcessName,Id,@{n='startUtc';e={if($null -ne $_.StartTime){$_.StartTime.ToUniversalTime().ToString('o')}else{$null}}} | ConvertTo-Json -Compress"
    result = subprocess.run(["powershell.exe", "-NoLogo", "-NoProfile", "-Command", command], text=True, capture_output=True, timeout=15)
    return json.loads(result.stdout) if result.stdout.strip() else {"unavailable": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sampler", type=Path, default=Path("reports/virtual-desktop-physical-motion.jsonl"))
    parser.add_argument("--scene", type=Path, default=Path("reports/virtual-desktop-controlled-scene-fixed.jsonl"))
    parser.add_argument("--epoch", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--human-result", default="Not supplied; no human pass inferred")
    parser.add_argument("--process-snapshot", action="store_true")
    args = parser.parse_args()
    cm.math_checks()
    assert abs(angular_diameter([(1, 0, 0, 0), (math.sqrt(.5), 0, math.sqrt(.5), 0)]) - 90) < 1e-10
    assert position_diameter([(0, 0, 0), (1, 0, 0)]) == 1
    sampler = load_capture(args.sampler, "sample")
    scene = load_capture(args.scene, "frame")
    raw_f = sampler["rows"]
    raw_s = [row for row in scene["rows"] if row.get("driver", {}).get("epoch") == args.epoch]
    if not raw_f or not raw_s:
        raise ValueError("Required sampler samples or scene epoch absent")
    header = next(row for row in sampler["meta"] if row["event"] == "header")
    frequency = header["qpcFrequency"]
    f = [canonical(row, False, raw_f[0]["qpc"], frequency) for row in raw_f]
    s = [canonical(row, True, raw_f[0]["qpc"], frequency) for row in raw_s]
    counter_index = {row["driverCapture"]["physicalSamples"]: row for row in raw_f}
    anchors, position_errors, quaternion_errors = [], [], []
    for row in raw_s:
        driver = row["driver"]
        other = counter_index.get(driver["physicalSamples"])
        if other is None or not driver["capturedPhysicalHeadAvailable"] or not other["driverCapture"]["capturedHeadValid"]:
            continue
        source = other["driverCapture"]
        anchors.append((source["driverQpc"] / frequency - source["headAgeMs"] / 1000) - (row["elapsedSeconds"] - driver["statusAgeMilliseconds"] / 1000 - driver["headAgeMilliseconds"] / 1000))
        position_errors.append(math.dist(source["capturedWorldPosition"], driver["capturedPhysicalHeadWorldPosition"]))
        quaternion_errors.append(cm.quaternion_angle_degrees(source["capturedWorldQuaternionWxyz"], driver["capturedPhysicalHeadWorldQuaternionWxyz"]))
    alignment = {"method": "Equal physicalSamples counters align independently captured source samples; source QPC is driverQpc minus head age, and scene-relative source time is elapsedSeconds minus status age minus head age", "sharedPhysicalSampleCountAnchors": len(anchors)}
    if anchors:
        offset = statistics.median(anchors)
        start = raw_f[0]["qpc"] / frequency - offset
        end = raw_f[-1]["qpc"] / frequency - offset
        overlap = [row for row in s if start <= row["t"] <= end]
        after = [row for row in s if row["t"] > end]
        alignment.update(
            absoluteQpcSecondsMinusSceneElapsedSecondsMedian=offset,
            anchorOffsetMinSeconds=min(anchors), anchorOffsetMaxSeconds=max(anchors),
            anchorOffsetSpreadMilliseconds=(max(anchors) - min(anchors)) * 1000,
            maximumMatchedSourcePositionDifferenceMeters=max(position_errors),
            maximumMatchedSourceQuaternionDifferenceDegrees=max(quaternion_errors),
            samplerSceneElapsedStartSeconds=start, samplerSceneElapsedEndSeconds=end,
            sceneSamplesOverlappingSampler=len(overlap),
            extendedSceneSecondsAfterSamplerEnd=s[-1]["t"] - end,
            sceneDuringSamplerMotion=motion(overlap), sceneAfterSamplerMotion=motion(after),
        )
    sampler_summary = {
        "headerRequestedSeconds": header["seconds"], "qpcFrequency": frequency,
        "firstSampleQpc": raw_f[0]["qpc"], "lastSampleQpc": raw_f[-1]["qpc"],
        "allSamplesProtocolValid": all(row["driverCapture"]["protocolValid"] for row in raw_f),
        "allSamplesPoseHookPresent": all(row["driverCapture"]["poseHookPresent"] for row in raw_f),
        "integrity": integrity(f), "motion": motion(f),
        "routedPlateaus": plateaus(f, args.epoch, include_details=True),
    }
    end_records = [row for row in sampler["meta"] if row["event"] == "end"]
    if end_records:
        end_qpc = end_records[-1]["qpc"]
        write_utc = datetime.datetime.fromtimestamp(args.sampler.stat().st_mtime, datetime.timezone.utc)
        sampler_summary.update(
            endRecordQpc=end_qpc,
            approximateFirstSampleUtcFromFinalWrite=(write_utc - datetime.timedelta(seconds=(end_qpc - raw_f[0]["qpc"]) / frequency)).isoformat(),
            approximateLastSampleUtcFromFinalWrite=(write_utc - datetime.timedelta(seconds=(end_qpc - raw_f[-1]["qpc"]) / frequency)).isoformat(),
        )
    changes = sampler_summary["routedPlateaus"]["changesWithSameRecordedDesktopModeAndEpoch"]
    report = {
        "schemaVersion": 1, "dateUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "evidence": "Independent read-only sampler and controlled-scene numerical analysis",
        "humanResult": args.human_result, "analyzedEpoch": args.epoch,
        "sources": [dict(path=str(path).replace("\\", "/"), **{key: value for key, value in capture.items() if key not in ("rows", "meta")}) for path, capture in ((args.sampler, sampler), (args.scene, scene))],
        "method": "Exhaustive maximum pairwise normalized-quaternion SO(3) angular separation and Euclidean position diameter, using bounded-memory NumPy blocks; exact serialized-matrix equality defines consecutive plateaus. Known identity, 90-degree, quaternion-sign, matrix and 1-meter checks passed.",
        "sampler": sampler_summary,
        "extendedSceneEpoch": {
            "integrity": integrity(s), "motion": motion(s),
            "bothEyeSubmitFailureRecordedSamples": sum(row["leftSubmitError"] != 0 or row["rightSubmitError"] != 0 for row in raw_s),
            "routedPlateaus": plateaus(s, args.epoch),
            "lastRecordedSceneElapsedSecondsInSnapshot": scene["rows"][-1]["elapsedSeconds"],
        },
        "timeAlignment": alignment,
        "interpretation": [
            f"The sampler records {changes} changes in routed output while sampled mode remains Desktop and acknowledgement epoch remains {args.epoch}. This establishes observed same-epoch output variation; plateaus and jumps can corroborate relatching behavior but cannot identify its implementation cause.",
            "A negative-age race cannot be established from unlogged timing. Recorded negative ages, invalid captures, status failures and client tracking failures are counted explicitly; their absence does not exclude brief states between samples.",
            "If the scene continues after the sampler's final sample, a delayed human action can fall outside the sampler. Human prompt/action timestamps are absent from these files, so delayed action is not established.",
        ],
        "limits": [
            "The supplied human result is retained independently of numerical results. This script never promotes observed motion or Submit success to a human test pass.",
            "Source world capture and routed standing output use different coordinate frames and independently timed reads. Only within-stream motion ranges and aligned intervals are compared; absolute world pose is never subtracted from standing pose.",
            "Plateaus use exact equality at each capture's serialization precision. Metrics apply only to sampled times; brief race states and tracking failures between samples may be missed.",
            "Approximate UTC bounds use final file LastWriteTime as the end-record wall-clock anchor and can be shifted by write/flush delay. QPC duration and cross-capture relative alignment are stronger evidence.",
            "Process presence does not prove loaded DLL hash or historical runtime identity. No configuration, runtime, application process, driver, or source code is changed by this analysis.",
        ],
    }
    if args.process_snapshot:
        report["currentProcessSnapshot"] = process_snapshot()
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"report": str(args.output), "humanResult": report["humanResult"], "samplerIntegrity": integrity(f), "samplerMotion": motion(f), "samplerMatrixChanges": changes, "sceneIntegrity": integrity(s), "sceneMotion": report["extendedSceneEpoch"]["motion"], "sceneMatrixChanges": report["extendedSceneEpoch"]["routedPlateaus"]["routedMatrixChangeCount"], "timeAlignment": alignment}, indent=2))


if __name__ == "__main__":
    main()
