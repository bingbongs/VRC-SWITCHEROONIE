"""Analyze an existing controlled-scene capture without contacting the VR runtime."""

import collections
import datetime
import hashlib
import itertools
import json
import math
from pathlib import Path
import subprocess
import sys


def normalized_quaternion(value):
    if len(value) != 4 or not all(math.isfinite(x) for x in value):
        raise ValueError("Invalid finite WXYZ quaternion")
    norm = math.sqrt(sum(x * x for x in value))
    if norm < 1e-12:
        raise ValueError("Zero quaternion")
    return tuple(x / norm for x in value)


def quaternion_angle_degrees(a, b):
    """Shortest SO(3) separation; q and -q represent the same rotation."""
    a, b = normalized_quaternion(a), normalized_quaternion(b)
    if sum(x * y for x, y in zip(a, b)) < 0:
        b = tuple(-x for x in b)
    difference = math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))
    total = math.sqrt(sum((x + y) ** 2 for x, y in zip(a, b)))
    return math.degrees(4 * math.atan2(difference, total))


def matrix_quaternion(matrix):
    """Convert the rotation block of row-major OpenVR HmdMatrix34_t to WXYZ."""
    if len(matrix) != 12 or not all(math.isfinite(x) for x in matrix):
        raise ValueError("Invalid finite 3x4 matrix")
    m00, m01, m02 = matrix[0:3]
    m10, m11, m12 = matrix[4:7]
    m20, m21, m22 = matrix[8:11]
    trace = m00 + m11 + m22
    if trace > 0:
        s = 2 * math.sqrt(trace + 1)
        q = (s / 4, (m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s)
    elif m00 > m11 and m00 > m22:
        s = 2 * math.sqrt(1 + m00 - m11 - m22)
        q = ((m21 - m12) / s, s / 4, (m01 + m10) / s, (m02 + m20) / s)
    elif m11 > m22:
        s = 2 * math.sqrt(1 + m11 - m00 - m22)
        q = ((m02 - m20) / s, (m01 + m10) / s, s / 4, (m12 + m21) / s)
    else:
        s = 2 * math.sqrt(1 + m22 - m00 - m11)
        q = ((m10 - m01) / s, (m02 + m20) / s, (m12 + m21) / s, s / 4)
    return normalized_quaternion(q)


def diameter(values, metric):
    return max((metric(a, b) for a, b in itertools.combinations(values, 2)), default=0.0)


def axis_ranges(positions):
    return [max(p[i] for p in positions) - min(p[i] for p in positions) for i in range(3)]


def math_checks():
    identity = (1, 0, 0, 0)
    yaw90 = (math.sqrt(0.5), 0, math.sqrt(0.5), 0)
    assert abs(quaternion_angle_degrees(identity, yaw90) - 90) < 1e-10
    assert quaternion_angle_degrees(yaw90, tuple(-x for x in yaw90)) == 0
    assert diameter([(0, 0, 0), (1, 0, 0)], math.dist) == 1
    assert axis_ranges([(0, -2, 1), (1, 3, 1)]) == [1, 5, 0]
    assert quaternion_angle_degrees(matrix_quaternion([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0]), identity) == 0
    assert abs(quaternion_angle_degrees(matrix_quaternion([0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, 0]), yaw90)) < 1e-10


def streamer_metadata():
    # Only executable version metadata and process presence. No command lines or user IDs.
    command = r"""
$ErrorActionPreference='Stop'
$item=Get-Item -LiteralPath 'C:\Program Files\Virtual Desktop Streamer\VirtualDesktop.Streamer.exe'
$process=Get-Process -Name VirtualDesktop.Streamer -ErrorAction SilentlyContinue | Select-Object -First 1
$processPath=if($null -ne $process){$process.Path}else{$null}
[pscustomobject]@{
  source='Fresh installed executable VersionInfo read'
  installedExecutable=$item.FullName
  fileVersion=$item.VersionInfo.FileVersion
  productVersion=$item.VersionInfo.ProductVersion
  installedExecutableLastWriteUtc=$item.LastWriteTimeUtc.ToString('o')
  runningProcessObserved=($null -ne $process)
  runningExecutablePathAvailable=![string]::IsNullOrEmpty($processPath)
  runningExecutableMatchesInstalledPath=if([string]::IsNullOrEmpty($processPath)){$null}else{$processPath -eq $item.FullName}
} | ConvertTo-Json -Compress
"""
    result = subprocess.run(["powershell.exe", "-NoLogo", "-NoProfile", "-Command", command], capture_output=True, text=True, timeout=15)
    if result.returncode:
        return {"source": "Fresh executable metadata read attempted", "available": False}
    return json.loads(result.stdout)


def main():
    math_checks()
    root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
    capture = root / "reports/virtual-desktop-controlled-scene-fixed.jsonl"
    cycles_path = root / "reports/virtual-desktop-25-cycles.json"
    raw = capture.read_bytes()
    complete_end = raw.rfind(b"\n") + 1
    snapshot = raw[:complete_end]
    records = [json.loads(line) for line in snapshot.decode("utf-8-sig").splitlines() if line.strip()]
    cycles_bytes = cycles_path.read_bytes()
    cycles = json.loads(cycles_bytes.decode("utf-8-sig"))
    frames = [row for row in records if row.get("event") == "frame"]
    starts = [row for row in records if row.get("event") == "start"]
    expected_pid = cycles["sceneProcessId"]
    transitions = cycles["transitions"]
    desktop_epochs = {row["epoch"] for row in transitions if row["mode"] == "Desktop"}
    grouped = collections.defaultdict(list)
    for row in frames:
        if row.get("driver", {}).get("mode") == 1 and row["driver"]["epoch"] in desktop_epochs:
            grouped[row["driver"]["epoch"]].append(row)
    frame_index = {row["frame"]: row for row in frames}
    ack_sample_matches = []
    for transition in transitions:
        row = frame_index.get(transition["sceneFrame"])
        expected_mode = 1 if transition["mode"] == "Desktop" else 0
        ack_sample_matches.append(bool(row and row["processId"] == expected_pid and row["driver"]["epoch"] == transition["epoch"] and row["driver"]["mode"] == expected_mode and row["leftSubmitError"] == 0 and row["rightSubmitError"] == 0))
    summaries = []
    for epoch in sorted(desktop_epochs):
        samples = grouped[epoch]
        retained = samples[1:]  # Remove the first recorded sample, even if already settled.
        item = {"epoch": epoch, "recordedSamples": len(samples), "retainedSamples": len(retained), "eligible": len(retained) >= 2}
        if samples:
            item.update(firstFrame=samples[0]["frame"], lastFrame=samples[-1]["frame"])
        if len(retained) < 2:
            item["limitation"] = "Fewer than two samples remain after first-sample exclusion"
            summaries.append(item)
            continue
        if not all(row["driver"]["capturedPhysicalHeadAvailable"] and row["headValid"] and row["driver"]["statusAlive"] for row in retained):
            raise ValueError("Unavailable pose or status in retained epoch")
        sources_q = [row["driver"]["capturedPhysicalHeadWorldQuaternionWxyz"] for row in retained]
        sources_p = [row["driver"]["capturedPhysicalHeadWorldPosition"] for row in retained]
        matrices = [row["routedHeadStandingMatrix"] for row in retained]
        routed_q = [matrix_quaternion(matrix) for matrix in matrices]
        routed_p = [[matrix[3], matrix[7], matrix[11]] for matrix in matrices]
        item.update(
            retainedDurationSeconds=retained[-1]["elapsedSeconds"] - retained[0]["elapsedSeconds"],
            sourceAngularDiameterDegrees=diameter(sources_q, quaternion_angle_degrees),
            sourcePositionDiameterMeters=diameter(sources_p, math.dist),
            sourcePositionAxisRangeMeters=axis_ranges(sources_p),
            routedAngularDiameterDegrees=diameter(routed_q, quaternion_angle_degrees),
            routedPositionDiameterMeters=diameter(routed_p, math.dist),
            routedPositionAxisRangeMeters=axis_ranges(routed_p),
            routedMatrixMaxElementVariation=diameter(matrices, lambda a, b: max(abs(x - y) for x, y in zip(a, b))),
            physicalSampleCounterAdvanced=retained[-1]["driver"]["physicalSamples"] > retained[0]["driver"]["physicalSamples"],
            routedSampleCounterAdvanced=retained[-1]["driver"]["routedSamples"] > retained[0]["driver"]["routedSamples"],
        )
        if item["routedMatrixMaxElementVariation"] != 0:
            item["routedChangedFromFirstRetainedFrames"] = [row["frame"] for row in retained if row["routedHeadStandingMatrix"] != matrices[0]]
        summaries.append(item)
    eligible = [item for item in summaries if item["eligible"]]
    desktop_frames = [row for samples in grouped.values() for row in samples]
    cycle_window = [row for row in frames if min(row["frame"] for row in desktop_frames) <= row["frame"] <= max(row["sceneFrame"] for row in transitions)]
    report = {
        "schemaVersion": 1,
        "dateUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "evidence": "Read-only numerical analysis of existing controlled-scene samples; incidental physical pose variation",
        "sources": {
            "capture": str(capture.relative_to(root)).replace("\\", "/"),
            "completeLineSnapshotSha256": hashlib.sha256(snapshot).hexdigest(),
            "completeLineSnapshotBytes": len(snapshot),
            "incompleteTrailingBytesExcluded": len(raw) - complete_end,
            "cycleReport": str(cycles_path.relative_to(root)).replace("\\", "/"),
            "cycleReportSha256": hashlib.sha256(cycles_bytes).hexdigest(),
        },
        "method": {
            "scope": "25 Desktop epochs declared by the 50-transition cycle report",
            "firstRecordedSampleExcludedPerEpoch": True,
            "minimumRetainedSamples": 2,
            "angularMetric": "Maximum pairwise normalized WXYZ quaternion shortest SO(3) separation in degrees, treating q and -q as identical",
            "positionMetric": "Maximum pairwise Euclidean distance plus per-axis max-minus-min, in meters",
            "routedMetric": "Same pose metrics on each row-major HmdMatrix34_t rotation and translation block; also maximum absolute element difference",
            "coordinateHandling": "Compare variation within each source/routed stream separately; never subtract source world pose from routed standing pose or compare absolute origins",
            "mathKnownCaseChecks": "Passed: identity, 90-degree yaw, quaternion sign equivalence, 1-meter distance, matrix conversion, axis ranges",
        },
        "recordedContinuity": {
            "declaredSceneProcessId": expected_pid,
            "startRecords": len(starts),
            "allStartsDeclareExpectedProcess": bool(starts) and all(row["processId"] == expected_pid for row in starts),
            "allRecordedFrameProcessIds": sorted({row["processId"] for row in frames}),
            "allRecordedFramesDeclareExpectedProcess": all(row["processId"] == expected_pid for row in frames),
            "totalRecordedFrames": len(frames),
            "frameNumbersStrictlyAdvance": all(b["frame"] > a["frame"] for a, b in zip(frames, frames[1:])),
            "elapsedSecondsStrictlyAdvance": all(b["elapsedSeconds"] > a["elapsedSeconds"] for a, b in zip(frames, frames[1:])),
            "cycleReportTransitions": len(transitions),
            "acknowledgementFrameMatchesEpochModeProcessAndSuccessfulBothEyeSubmit": sum(ack_sample_matches),
            "cycleWindowRecordedFrames": len(cycle_window),
            "cycleWindowBothEyeSubmitErrorZero": all(row["leftSubmitError"] == 0 and row["rightSubmitError"] == 0 for row in cycle_window),
            "desktopRecordedFrames": len(desktop_frames),
            "desktopBothEyeSubmitErrorZero": all(row["leftSubmitError"] == 0 and row["rightSubmitError"] == 0 for row in desktop_frames),
            "allCaptureRecordedSubmitFailureCount": sum(row["leftSubmitError"] != 0 or row["rightSubmitError"] != 0 for row in frames),
        },
        "summary": {
            "requestedDesktopEpochs": len(desktop_epochs),
            "epochsPresent": len(grouped),
            "eligibleEpochs": len(eligible),
            "insufficientSampleEpochs": len(summaries) - len(eligible),
            "retainedSamplesInEligibleEpochs": sum(item["retainedSamples"] for item in eligible),
            "maxSourceAngularDiameterDegrees": max(item["sourceAngularDiameterDegrees"] for item in eligible),
            "maxSourcePositionDiameterMeters": max(item["sourcePositionDiameterMeters"] for item in eligible),
            "maxRoutedAngularDiameterDegrees": max(item["routedAngularDiameterDegrees"] for item in eligible),
            "maxRoutedPositionDiameterMeters": max(item["routedPositionDiameterMeters"] for item in eligible),
            "maxRoutedMatrixElementVariation": max(item["routedMatrixMaxElementVariation"] for item in eligible),
            "eligibleEpochsWithExactlyConstantSerializedRoutedMatrix": sum(item["routedMatrixMaxElementVariation"] == 0 for item in eligible),
            "eligibleEpochsWithNonzeroRoutedVariation": [item["epoch"] for item in eligible if item["routedMatrixMaxElementVariation"] != 0],
            "epochWithMaxSourceAngularAndPositionDiameters": max(eligible, key=lambda item: item["sourceAngularDiameterDegrees"])["epoch"] if max(eligible, key=lambda item: item["sourceAngularDiameterDegrees"])["epoch"] == max(eligible, key=lambda item: item["sourcePositionDiameterMeters"])["epoch"] else None,
            "epochsWithPhysicalSampleCounterAdvance": sum(item["physicalSampleCounterAdvanced"] for item in eligible),
            "epochsWithRoutedSampleCounterAdvance": sum(item["routedSampleCounterAdvanced"] for item in eligible),
        },
        "epochs": summaries,
        "freshStreamerExecutableMetadata": streamer_metadata(),
        "limits": [
            "Incidental source pose movement in this capture is not a deliberately instructed F1 motion experiment; F1 remains unperformed by this analysis.",
            "The source is pre-output driver world space and the routed matrix is post-routing standing space; coordinate origins differ. Only within-epoch variation is compared.",
            "Matrix constancy is measured at the JSON serialization precision and recorded sampling times; it cannot establish behavior between samples or outside the eligible epochs.",
            "Epoch 1384081817952 has nonzero routed variation at its final recorded Desktop sample, frame 11941; its cause is not determined. Pose retrieval and subsequent shared driver-status read are asynchronous, so a late transition overlap cannot be excluded.",
            "Same process ID and advancing frame/time values are declared by the recorded scene; this analysis does not independently establish historical OS process identity.",
            "Submit error zero is observed at recorded samples and does not alone prove headset presentation, deliberate motion suppression, VRChat menus/movement/multiplayer, or cold start.",
            "Streamer executable FileVersion is fresh installed-file metadata. It is distinct from SteamVR transport driver compatibility/version; unavailable running-process paths prevent confirming that exact executable is the running image.",
        ],
    }
    output = root / "reports/live-capture-independence.json"
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(output.relative_to(root)), "recordedContinuity": report["recordedContinuity"], "summary": report["summary"], "freshStreamerExecutableMetadata": report["freshStreamerExecutableMetadata"]}, indent=2))


if __name__ == "__main__":
    main()
