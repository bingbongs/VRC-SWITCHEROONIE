# Actual broker/helper process integration

```powershell
dotnet run --project .\tests\IntegrationHarness\IntegrationHarness.csproj -c Release
```

Run from the repository root or pass its path as the first program argument. The project references Broker/Common so it builds the Release executable before testing. Results are written to `reports/process-integration.json`.

To exercise the actual packaged self-contained executable, pass its absolute path as a second argument:

```powershell
dotnet run --project .\tests\IntegrationHarness\IntegrationHarness.csproj -c Release -- "$PWD" "$PWD\dist\VRC-SWITCHEROONIE-0.1.0\Switcheroonie.Broker.exe"
```

The report records the tested artifact's relative path and SHA-256. In this explicit packaged mode, owned child processes receive a nonexistent external `DOTNET_ROOT`, disabled multilevel lookup/roll-forward, and the harness verifies `coreclr.dll` loaded from the package directory. These environment settings apply only to test-owned processes.

The harness starts only its own broker/helper executables, hidden. It refuses public-pipe testing when a pre-existing broker/helper or SteamVR server is running. Public tests verify malformed/oversized message rejection, eight idle connections plus a ninth busy response, recovery, and emergency release with the driver absent. It captures the broker's exact helper process handles before stopping the parent and then its children.

OSC tests run a separate real helper process with an isolated randomly named map and an ephemeral mock UDP receiver. A simulated driver status and producer lease are published only to that private map. No public driver status is spoofed and no packets are sent to VRChat's port. The tests verify real float/integer packets, neutral packets after a producer heartbeat stops, same-value rearm, and baseline neutral packets from a replacement helper that inherited a disarmed lease. The stale-lease test records elapsed time; its 450 ms ceiling allows Windows process/UDP scheduling on top of the 200 ms lease. It does not certify physical or application input latency.

An additional private-map test constructs the live managed broker engine, expires a simulated helper heartbeat, and verifies its emergency sender emits actual neutral UDP even after the producer is disarmed. This covers the backup sender's implementation without taking focus or spoofing the public driver map; full UI/physical tracking behavior remains a hardware test.

The test that kills the only sender records an `ObservedLimit`, not a release pass: no surviving process can send neutral OSC. Physical tracking, VRChat receipt, menu actions, application continuity, and actual headset display remain hardware tests. Tests skipped to protect a pre-existing session are `NotRun`, never passed.
