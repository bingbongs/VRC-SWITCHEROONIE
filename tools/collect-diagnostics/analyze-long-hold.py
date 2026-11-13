"""Stream a fixed-size scene-log snapshot in O(N) time and bounded memory."""

import argparse
import collections
import datetime
import hashlib
import importlib.util
import json
import math
from pathlib import Path

spec = importlib.util.spec_from_file_location("capture_math", Path(__file__).with_name("analyze-live-capture.py"))
cm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cm)


class BoundedUnique:
    def __init__(self, limit=4096):
        self.limit = limit
        self.values = set()
        self.sample_counts = {}
        self.saturated = False

    def add(self, value):
        if self.saturated:
            return
        value = tuple(value)
        if value in self.values:
            self.sample_counts[value] += 1
            return
        if len(self.values) == self.limit:
            self.saturated = True
        else:
            self.values.add(value)
            self.sample_counts[value] = 1

    def report(self):
        return {"count": len(self.values) + int(self.saturated), "isExact": not self.saturated, "meaning": "Exact count" if not self.saturated else "Lower bound after bounded uniqueness capacity reached", "sampleCountsInFirstSeenOrder": list(self.sample_counts.values())[:16], "sampleCountListTruncated": len(self.sample_counts) > 16 or self.saturated}


class PoseDeviation:
    def __init__(self):
        self.count = 0
        self.first_position = None
        self.first_quaternion = None
        self.first_index = None
        self.max_angle = 0.0
        self.max_distance = 0.0
        self.max_angle_index = None
        self.max_distance_index = None
        self.minimum = [math.inf] * 3
        self.maximum = [-math.inf] * 3

    def add(self, position, quaternion, index):
        if position is None or quaternion is None:
            return
        if not all(math.isfinite(value) for value in position + quaternion):
            raise ValueError("Nonfinite recorded pose")
        self.count += 1
        if self.first_position is None:
            self.first_position, self.first_quaternion, self.first_index = position, quaternion, index
        angle = cm.quaternion_angle_degrees(self.first_quaternion, quaternion) if self.first_quaternion != quaternion else 0.0
        distance = math.dist(self.first_position, position)
        if angle > self.max_angle:
            self.max_angle, self.max_angle_index = angle, index
        if distance > self.max_distance:
            self.max_distance, self.max_distance_index = distance, index
        for axis in range(3):
            self.minimum[axis] = min(self.minimum[axis], position[axis])
            self.maximum[axis] = max(self.maximum[axis], position[axis])

    def report(self):
        return {
            "validSamples": self.count, "firstValidFrame": self.first_index,
            "maxAngularDeviationDegreesFromFirstValidPose": self.max_angle if self.count else None,
            "maxPositionDeviationMetersFromFirstValidPose": self.max_distance if self.count else None,
            "frameWithMaxAngularDeviation": self.max_angle_index,
            "frameWithMaxPositionDeviation": self.max_distance_index,
            "axisRangesMeters": [self.maximum[i] - self.minimum[i] for i in range(3)] if self.count else None,
            "metricIsPairwiseDiameter": False,
        }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scene", type=Path, required=True)
    parser.add_argument("--epoch", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--human-reply", default="Not supplied")
    parser.add_argument("--utc-anchor-sampler", type=Path)
    parser.add_argument("--separate-human-results", type=Path)
    args = parser.parse_args()
    cm.math_checks()
    source, routed, synthetic = PoseDeviation(), PoseDeviation(), PoseDeviation()
    unique_routed, unique_synthetic = BoundedUnique(), BoundedUnique()
    pids, modes, errors, anchors, synthetic_epochs = (collections.Counter() for _ in range(5))
    counters = collections.Counter()
    first = last = previous = None
    previous_matrix = previous_synthetic_pose = previous_synthetic_qpc = None
    age_min, age_max, status_age_max = math.inf, -math.inf, -math.inf
    synthetic_age_min, synthetic_age_max = math.inf, -math.inf
    max_gap, max_synthetic_qpc_gap = 0.0, 0
    snapshot_size = args.scene.stat().st_size
    digest = hashlib.sha256()
    consumed = total_frames = complete_lines = incomplete_bytes = 0
    first_start = None
    following_epoch_frame = None
    recent_reference_events = collections.deque(maxlen=128)
    reference_event_counts = collections.Counter()
    reference_event_frames = []
    routed_change_events = []
    active_recorded_epoch = None
    wallclock_anchor = None
    if args.utc_anchor_sampler:
        anchor_header = anchor_end = None
        with args.utc_anchor_sampler.open("rb") as anchor_stream:
            for anchor_line in anchor_stream:
                anchor_row = json.loads(anchor_line.decode("utf-8-sig"))
                if anchor_row.get("event") == "header":
                    anchor_header = anchor_row
                elif anchor_row.get("event") == "end":
                    anchor_end = anchor_row
        if anchor_header and anchor_end:
            wallclock_anchor = {"source": str(args.utc_anchor_sampler).replace("\\", "/"), "qpc": anchor_end["qpc"], "frequency": anchor_header["qpcFrequency"], "utc": datetime.datetime.fromtimestamp(args.utc_anchor_sampler.stat().st_mtime, datetime.timezone.utc)}
    with args.scene.open("rb") as stream:
        while consumed < snapshot_size:
            line = stream.readline(snapshot_size - consumed)
            if not line:
                break
            consumed += len(line)
            if not line.endswith(b"\n"):
                incomplete_bytes = len(line)
                break
            digest.update(line)
            complete_lines += 1
            if not line.strip():
                continue
            row = json.loads(line.decode("utf-8-sig"))
            if row.get("event") == "runtime-device-event" and row.get("eventType") in (804, 805, 808):
                reference_event = {"frame": row["frame"], "eventType": row["eventType"], "deviceIndex": row["deviceIndex"]}
                recent_reference_events.append(reference_event)
                if active_recorded_epoch == args.epoch:
                    reference_event_counts[str(row["eventType"])] += 1
                    if row["eventType"] == 808 and len(reference_event_frames) < 64:
                        reference_event_frames.append(row["frame"])
                for change in routed_change_events:
                    if row["eventType"] == 808 and change.get("nearestFollowingStandingZeroPoseReset") is None and 0 <= row["frame"] - change["frame"] <= 500:
                        change["nearestFollowingStandingZeroPoseReset"] = reference_event
            if row.get("event") == "start" and first_start is None:
                first_start = {key: row.get(key) for key in ("processId", "qpcFrequency", "trackingUniverse", "source", "driverCapturedPoseSpace")}
            if row.get("event") != "frame":
                continue
            total_frames += 1
            driver = row.get("driver", {})
            active_recorded_epoch = driver.get("epoch")
            if driver.get("epoch") != args.epoch:
                if last is not None and following_epoch_frame is None:
                    following_epoch_frame = {"frame": row["frame"], "elapsedSeconds": row["elapsedSeconds"], "epoch": driver.get("epoch"), "mode": driver.get("mode")}
                continue
            counters["samples"] += 1
            index = row["frame"]
            current = {"frame": index, "elapsedSeconds": row["elapsedSeconds"], "physicalSamples": driver["physicalSamples"], "routedSamples": driver["routedSamples"]}
            if first is None:
                first = current
            if previous is not None:
                gap = current["elapsedSeconds"] - previous["elapsedSeconds"]
                max_gap = max(max_gap, gap)
                counters["gapsOver500Milliseconds"] += gap > .5
                counters["nonadvancingFrameNumbers"] += index <= previous["frame"]
                counters["nonadvancingElapsedSeconds"] += gap <= 0
                counters["physicalCounterNonadvancingIntervals"] += current["physicalSamples"] <= previous["physicalSamples"]
                counters["routedCounterNonadvancingIntervals"] += current["routedSamples"] <= previous["routedSamples"]
            last = previous = current
            pids[str(row["processId"])] += 1
            modes[str(driver["mode"])] += 1
            errors[str(driver["error"])] += 1
            if driver.get("anchorEpoch") is not None:
                anchors[str(driver["anchorEpoch"])] += 1
                counters["anchorEpochMismatchSamples"] += driver["anchorEpoch"] != args.epoch
            counters["invalidClientHeadSamples"] += not row["headValid"]
            counters["disconnectedClientHeadSamples"] += not row["headConnected"]
            counters["leftSubmitFailureSamples"] += row["leftSubmitError"] != 0
            counters["rightSubmitFailureSamples"] += row["rightSubmitError"] != 0
            counters["nonfreshDriverStatusSamples"] += not driver["statusAlive"]
            counters["poseHookMissingSamples"] += (driver.get("capabilityFlags", 0) & 1) == 0
            age = driver["headAgeMilliseconds"]
            age_min, age_max = min(age_min, age), max(age_max, age)
            status_age_max = max(status_age_max, driver["statusAgeMilliseconds"])
            counters["negativeHeadAgeSamples"] += age < 0
            counters["headAgeOver200MillisecondsSamples"] += age > 200
            if driver["capturedPhysicalHeadAvailable"]:
                source.add(driver["capturedPhysicalHeadWorldPosition"], driver["capturedPhysicalHeadWorldQuaternionWxyz"], index)
            else:
                counters["invalidPhysicalCaptureSamples"] += 1
            matrix = row["routedHeadStandingMatrix"]
            unique_routed.add(matrix)
            counters["routedMatrixChanges"] += previous_matrix is not None and matrix != previous_matrix
            if previous_matrix is not None and matrix != previous_matrix and len(routed_change_events) < 32:
                before_q, after_q = cm.matrix_quaternion(previous_matrix), cm.matrix_quaternion(matrix)
                change = {"frame": index, "elapsedSeconds": row["elapsedSeconds"], "beforeMatrix34": previous_matrix, "afterMatrix34": matrix, "angularJumpDegrees": cm.quaternion_angle_degrees(before_q, after_q), "positionJumpMeters": math.dist([previous_matrix[3], previous_matrix[7], previous_matrix[11]], [matrix[3], matrix[7], matrix[11]]), "rotationElementsChanged": any(matrix[i] != previous_matrix[i] for i in (0, 1, 2, 4, 5, 6, 8, 9, 10)), "translationElementsChanged": any(matrix[i] != previous_matrix[i] for i in (3, 7, 11)), "syntheticHeadEpoch": driver.get("syntheticHeadEpoch"), "anchorEpoch": driver.get("anchorEpoch"), "syntheticHeadQpc": driver.get("syntheticHeadQpc"), "nearbyPrecedingReferenceEvents": [event for event in recent_reference_events if 0 <= index - event["frame"] <= 120]}
                prior_resets = [event for event in recent_reference_events if event["eventType"] == 808 and event["frame"] <= index]
                change["nearestPrecedingStandingZeroPoseReset"] = prior_resets[-1] if prior_resets else None
                if driver.get("syntheticHeadQpc") is not None and driver.get("syntheticHeadAgeMilliseconds") is not None and first_start and first_start.get("qpcFrequency"):
                    estimated_qpc = driver["syntheticHeadQpc"] + round(driver["syntheticHeadAgeMilliseconds"] * first_start["qpcFrequency"] / 1000)
                    change["estimatedFrameQpcFromSyntheticAge"] = estimated_qpc
                    if wallclock_anchor:
                        change["approximateUtcFromSamplerFinalWrite"] = (wallclock_anchor["utc"] + datetime.timedelta(seconds=(estimated_qpc - wallclock_anchor["qpc"]) / wallclock_anchor["frequency"])).isoformat()
                routed_change_events.append(change)
            previous_matrix = matrix
            routed.add([matrix[3], matrix[7], matrix[11]], list(cm.matrix_quaternion(matrix)), index)
            if driver.get("routedSyntheticHeadAvailable") is not None:
                counters["syntheticFieldsSamples"] += 1
                synthetic_epochs[str(driver.get("syntheticHeadEpoch"))] += 1
                counters["syntheticEpochMismatchSamples"] += driver.get("syntheticHeadEpoch") != args.epoch
                syn_age = driver["syntheticHeadAgeMilliseconds"]
                synthetic_age_min, synthetic_age_max = min(synthetic_age_min, syn_age), max(synthetic_age_max, syn_age)
                counters["negativeSyntheticAgeSamples"] += syn_age < 0
                qpc = driver["syntheticHeadQpc"]
                if previous_synthetic_qpc is not None:
                    counters["syntheticQpcNonadvancingIntervals"] += qpc <= previous_synthetic_qpc
                    max_synthetic_qpc_gap = max(max_synthetic_qpc_gap, qpc - previous_synthetic_qpc)
                previous_synthetic_qpc = qpc
                if driver["routedSyntheticHeadAvailable"]:
                    syn_pose = driver["routedSyntheticWorldPosition"] + driver["routedSyntheticWorldQuaternionWxyz"]
                    unique_synthetic.add(syn_pose)
                    counters["syntheticPoseChanges"] += previous_synthetic_pose is not None and syn_pose != previous_synthetic_pose
                    previous_synthetic_pose = syn_pose
                    synthetic.add(driver["routedSyntheticWorldPosition"], driver["routedSyntheticWorldQuaternionWxyz"], index)
                else:
                    counters["invalidSyntheticHeadSamples"] += 1
    if first is None:
        raise ValueError("Requested epoch not present")
    frequency = first_start.get("qpcFrequency") if first_start else None
    report = {
        "schemaVersion": 1, "dateUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "evidence": "Read-only O(N) stream analysis of a fixed file-size long-hold snapshot",
        "humanResult": {"actualReply": args.human_reply, "recordedResult": "Repaired held-view pass only" if args.human_reply == "ccorrect" else "No pass inferred", "scope": "User confirmation after explicit monitor grid/cubes headset-movement instructions, supplied by the parent task", "reverseMouseLook": "Separate test; not established by this held epoch", "physicalReturn": "Separate test; not established by this held epoch", "continuousMultiHourHumanOrFullUsageTest": "Not claimed"},
        "source": {"path": str(args.scene).replace("\\", "/"), "initialFileSizeBytes": snapshot_size, "completeLineSnapshotBytes": consumed - incomplete_bytes, "completeLineSnapshotSha256": digest.hexdigest(), "incompleteTrailingBytesExcluded": incomplete_bytes, "completeLines": complete_lines, "allFrameRecordsInSnapshot": total_frames, "start": first_start},
        "heldEpoch": args.epoch,
        "recordedContinuity": {"first": first, "last": last, "elapsedSpanSeconds": last["elapsedSeconds"] - first["elapsedSeconds"], "recordedSamples": counters["samples"], "maxRecordedSampleGapSeconds": max_gap, "processIdCounts": dict(pids), "modeCounts": dict(modes), "driverErrorCounts": dict(errors), "anchorEpochCounts": dict(anchors), "syntheticEpochCounts": dict(synthetic_epochs), "headAgeMsMin": age_min, "headAgeMsMax": age_max, "statusAgeMsMax": status_age_max, "syntheticHeadAgeMsMin": synthetic_age_min if synthetic.count else None, "syntheticHeadAgeMsMax": synthetic_age_max if synthetic.count else None, "maxSyntheticQpcGapMilliseconds": max_synthetic_qpc_gap * 1000 / frequency if frequency else None, "followingRecordedEpoch": following_epoch_frame, "counters": dict(counters)},
        "sourcePhysicalDeviation": source.report(),
        "clientRoutedStandingDeviation": routed.report(),
        "forwardedSyntheticWorldDeviation": synthetic.report(),
        "uniqueSerializedClientRoutedMatrices": unique_routed.report(),
        "uniqueSerializedForwardedSyntheticWorldPoses": unique_synthetic.report(),
        "clientStandingOutputChanges": routed_change_events,
        "clientStandingOutputChangeListTruncated": counters["routedMatrixChanges"] > len(routed_change_events),
        "referenceResetEventsWhilePreviouslyRecordedEpochWasHeld": {"counts": dict(reference_event_counts), "standingZeroPoseResetFrames": reference_event_frames, "labelsFromPinnedOpenVrHeader": {"804": "VREvent_SeatedZeroPoseReset", "805": "VREvent_ChaperoneFlushCache", "808": "VREvent_StandingZeroPoseReset"}, "source": "third_party/openvr/headers/openvr.h", "epochAttribution": "Uses the most recently logged frame epoch; events have no own acknowledgement epoch"},
        "method": {"timeComplexity": "O(N); no all-pairs comparisons", "memory": "One JSON line, fixed statistics and up to 4096 unique poses per output stream; uniqueness becomes a declared lower bound if capacity is exceeded", "angularMetric": "Shortest normalized-quaternion SO(3) deviation from the first valid pose, treating q and -q as identical", "positionMetric": "Euclidean deviation from the first valid position and per-axis range within each stream", "coordinateHandling": "Source driver-world, synthetic driver-world and client-standing streams are measured separately; no absolute positions are subtracted across streams", "boundaryHandling": "Every recorded sample carrying this acknowledgement epoch is included; asynchronous pose/status overlap near a following transaction is not discarded"},
        "limits": ["The recorded multi-hour duration is automated sample continuity, not a human multi-hour stability or full-usage claim.", "The human reply confirms the instructed repaired held-view observation only. Reverse mouse-look, Physical return, VRChat behavior and other tests remain separate.", "Source motion values are maximum deviation from the first valid pose, not exhaustive pairwise diameters.", "Pose uniqueness and constancy apply at recorded JSON precision and sampling times. States between samples and after this fixed-size snapshot are not established.", "Scene pose retrieval and shared driver-status reads are asynchronous; a mixed sample at an epoch boundary can occur. Any recorded variation is retained in this report.", "Standing-zero-pose reset events tightly precede the standing-output changes while forwarded synthetic world pose remains constant. This supports a reference-transform explanation; the actual raw-to-standing transform was not logged, so cause is not proved.", "Frame QPC is estimated from logged synthetic QPC and synthetic age. Approximate UTC uses a completed sampler's final file LastWriteTime as its end-QPC anchor; write/flush delay can shift this wall-clock estimate.", "Earlier failed-F1 and small repaired analyses are preserved separately. This analysis makes no runtime, process, configuration, driver or prior-report changes."],
    }
    if args.separate_human_results:
        separate = json.loads(args.separate_human_results.read_text(encoding="utf-8-sig"))
        report["separateHumanResults"] = {"source": str(args.separate_human_results).replace("\\", "/"), "actualReply": separate.get("humanReply"), "result": separate.get("humanResult"), "scope": separate.get("scope"), "mouseLook": separate.get("mouseLook"), "physicalReturn": separate.get("physicalReturn"), "relationship": "Separate later test, not inferred from the held epoch analyzed here"}
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"report": str(args.output), "humanResult": report["humanResult"], "continuity": report["recordedContinuity"], "physicalSource": report["sourcePhysicalDeviation"], "clientRouted": report["clientRoutedStandingDeviation"], "forwardedSynthetic": report["forwardedSyntheticWorldDeviation"], "uniqueRouted": report["uniqueSerializedClientRoutedMatrices"], "uniqueSynthetic": report["uniqueSerializedForwardedSyntheticWorldPoses"]}, indent=2))


if __name__ == "__main__":
    main()
