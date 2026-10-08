param(
    [ValidateRange(1, 60)][int]$Seconds = 30,
    [ValidateRange(20, 1000)][int]$IntervalMilliseconds = 100,
    [string]$OutputPath,
    [string]$PipeName,
    [switch]$SkipProcessInventory
)
$ErrorActionPreference = 'Stop'

# Record-only: the sole wire command is GetStatus. No input APIs, action
# commands, process launches, mode selection, or runtime changes are used.
if ([string]::IsNullOrWhiteSpace($PipeName)) {
    $taskSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $PipeName = 'VRC-SWITCHEROONIE-' + $taskSid
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('VRC-SWITCHEROONIE\reports\direct-game-input-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff') + '.jsonl')
}
$taskLogPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskLogPath)) | Out-Null

function Get-AllowedLabel($Value, [string[]]$Allowed) {
    if ($Value -is [string] -and $Allowed -ccontains $Value) { return $Value }
    return 'Unknown'
}
function Get-Boolean($Value) {
    if ($Value -is [bool]) { return $Value }
    return $null
}
function Get-Unsigned($Value) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [bool]) { return $null }
    try {
        $taskNumber = [decimal]$Value
        if ($taskNumber -ge 0 -and $taskNumber -le [uint64]::MaxValue -and [decimal]::Truncate($taskNumber) -eq $taskNumber) { return [uint64]$taskNumber }
    } catch { }
    return $null
}
function Get-Age($Value) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [bool]) { return $null }
    try {
        $taskNumber = [double]$Value
        if (-not [double]::IsNaN($taskNumber) -and -not [double]::IsInfinity($taskNumber) -and $taskNumber -ge -1 -and $taskNumber -le 1000000000) { return $taskNumber }
    } catch { }
    return $null
}
function Get-BoundedUnsigned($Value, [uint64]$Maximum) {
    $taskCount = Get-Unsigned $Value
    if ($null -ne $taskCount -and $taskCount -le $Maximum) { return $taskCount }
    return $null
}
function Get-SpinSpeed($Value) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [bool]) { return $null }
    try {
        $taskNumber = [double]$Value
        if (-not [double]::IsNaN($taskNumber) -and -not [double]::IsInfinity($taskNumber) -and [Math]::Abs($taskNumber) -le 5.21) { return $taskNumber }
    } catch { }
    return $null
}
function Get-ProcessRecord([string]$Name) {
    $taskRecords = @()
    foreach ($taskProcess in @(Get-Process -Name $Name -ErrorAction SilentlyContinue)) {
        try {
            $taskRecords += [ordered]@{ pid = $taskProcess.Id; startedUtc = $taskProcess.StartTime.ToUniversalTime().ToString('o') }
        } catch { }
        finally { $taskProcess.Dispose() }
    }
    return $taskRecords
}
function Read-BrokerSnapshot([string]$Name, [int]$BudgetMilliseconds) {
    $taskPipe = New-Object IO.Pipes.NamedPipeClientStream('.', $Name, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $taskTimeout = New-Object Threading.CancellationTokenSource
    $taskTimeout.CancelAfter($BudgetMilliseconds)
    try {
        $taskPipe.ConnectAsync($BudgetMilliseconds, $taskTimeout.Token).GetAwaiter().GetResult()
        $taskRequest = [Text.Encoding]::UTF8.GetBytes('{"Version":1,"Id":"' + [Guid]::NewGuid().ToString('N') + '","Name":"GetStatus"}' + "`n")
        $taskPipe.WriteAsync($taskRequest, 0, $taskRequest.Length, $taskTimeout.Token).GetAwaiter().GetResult()
        $taskBytes = New-Object byte[] 32768
        $taskCount = 0
        while ($taskCount -lt $taskBytes.Length) {
            $taskRead = $taskPipe.ReadAsync($taskBytes, $taskCount, $taskBytes.Length - $taskCount, $taskTimeout.Token).GetAwaiter().GetResult()
            if ($taskRead -eq 0) { throw 'Incomplete status reply' }
            $taskNewline = [Array]::IndexOf($taskBytes, [byte]10, $taskCount, $taskRead)
            if ($taskNewline -ge 0) { $taskCount = $taskNewline; break }
            $taskCount += $taskRead
        }
        if ($taskCount -ge $taskBytes.Length) { throw 'Status reply exceeded its bound' }
        $taskUtf8 = New-Object Text.UTF8Encoding($false, $true)
        $taskReply = ConvertFrom-Json -InputObject $taskUtf8.GetString($taskBytes, 0, $taskCount)
        if ($null -eq $taskReply -or $taskReply.Accepted -isnot [bool] -or $null -eq $taskReply.Status -or
            $taskReply.Status -is [string] -or $taskReply.Status.DriverAlive -isnot [bool]) { throw 'Invalid status reply' }
        return $taskReply
    } finally {
        $taskPipe.Dispose(); $taskTimeout.Dispose()
    }
}

$taskClock = [Diagnostics.Stopwatch]::StartNew()
$taskWriter = New-Object IO.StreamWriter($taskLogPath, $false, (New-Object Text.UTF8Encoding($false)))
$taskWriter.AutoFlush = $true
$taskSamples = 0
$taskFailures = 0
$taskProcesses = $null
$taskNextProcessSample = 0
try {
    $taskWriter.WriteLine((ConvertTo-Json -Compress -Depth 8 -InputObject ([ordered]@{
        type = 'header'; schemaVersion = 1; utc = [DateTime]::UtcNow.ToString('o'); seconds = $Seconds; intervalMilliseconds = $IntervalMilliseconds
        evidence = 'Read-only status snapshots. Human confirmation is required for visible menu closing, movement and session continuity.'
        privacy = 'Only allowlisted state, counters, flags, ages and process PID/start time. No typed text, window titles, HWNDs, file paths, player/world IDs or free-form broker messages.'
    })))
    while ($taskClock.Elapsed.TotalSeconds -lt $Seconds) {
        $taskSampleStart = $taskClock.Elapsed.TotalMilliseconds
        $taskRemaining = [int][Math]::Floor($Seconds * 1000 - $taskSampleStart)
        if ($taskRemaining -lt 1) { break }
        if (-not $SkipProcessInventory -and $taskSampleStart -ge $taskNextProcessSample) {
            $taskProcesses = [ordered]@{
                vrchat = @(Get-ProcessRecord 'VRChat'); steamvrServer = @(Get-ProcessRecord 'vrserver'); steamvrCompositor = @(Get-ProcessRecord 'vrcompositor')
            }
            $taskNextProcessSample = $taskSampleStart + 1000
        }
        $taskRemaining = [int][Math]::Floor($Seconds * 1000 - $taskClock.Elapsed.TotalMilliseconds)
        if ($taskRemaining -lt 1) { break }
        $taskSample = [ordered]@{ type = 'sample'; utc = [DateTime]::UtcNow.ToString('o'); elapsedMilliseconds = [Math]::Round($taskSampleStart, 3); brokerResponding = $false; processes = $taskProcesses }
        try {
            $taskReply = Read-BrokerSnapshot $PipeName ([Math]::Min(750, $taskRemaining))
            $taskStatus = $taskReply.Status
            $taskSample.brokerResponding = $true
            $taskSample.accepted = $taskReply.Accepted
            $taskSample.service = Get-AllowedLabel $taskStatus.ServiceState @('Ready', 'Stopped')
            $taskSample.mode = Get-AllowedLabel $taskStatus.Mode @('Desktop', 'Physical', 'Unmanaged')
            $taskSample.state = Get-AllowedLabel $taskStatus.State @('Preparing', 'Idle', 'Desktop', 'WaitingForHeadset', 'PhysicalVR', 'Degraded')
            $taskSample.readiness = [ordered]@{
                routingReady = Get-Boolean $taskStatus.RoutingReady; driverAlive = Get-Boolean $taskStatus.DriverAlive
                hasHead = Get-Boolean $taskStatus.HasHead; hasLeft = Get-Boolean $taskStatus.HasLeft; hasRight = Get-Boolean $taskStatus.HasRight
                headAgeMilliseconds = Get-Age $taskStatus.HeadAgeMilliseconds; epoch = Get-Unsigned $taskStatus.Epoch
            }
            $taskSample.control = [ordered]@{
                owner = Get-AllowedLabel $taskStatus.InputOwner @('Game', 'Pad', 'MenuPulse', 'Released')
                armed = Get-Boolean $taskStatus.Armed; gameEnabled = Get-Boolean $taskStatus.GameInputEnabled
                gameActive = Get-Boolean $taskStatus.GameInputActive; cursorCaptured = Get-Boolean $taskStatus.GameCursorCaptured
                cursorHidden = Get-Boolean $taskStatus.GameCursorHidden
                cursorSurfaceVisible = Get-Boolean $taskStatus.GameCursorSurfaceVisible
                cursorSurfaceOwnsPoint = Get-Boolean $taskStatus.GameCursorSurfaceOwnsPoint
                cursorInfoAvailable = Get-Boolean $taskStatus.GameCursorInfoAvailable
                cursorHideAttempts = Get-Unsigned $taskStatus.GameCursorHideAttempts
                cursorHideObserved = Get-Unsigned $taskStatus.GameCursorHideObserved
                cursorWatchdogAlive = Get-Boolean $taskStatus.GameCursorWatchdogAlive; menuNavigation = Get-Boolean $taskStatus.MenuNavigation
                automaticEnabled = Get-Boolean $taskStatus.AutomaticEnabled; manualMode = Get-AllowedLabel $taskStatus.ManualMode @('Desktop', 'Physical')
                headsetWornKnown = Get-Boolean $taskStatus.HeadsetWornKnown; headsetWorn = Get-Boolean $taskStatus.HeadsetWorn
            }
            $taskSample.spin = [ordered]@{
                enabled = Get-Boolean $taskStatus.SpinEnabled; active = Get-Boolean $taskStatus.SpinActive
                nativeActive = Get-Boolean $taskStatus.NativeSpinActive
                blockReason = Get-BoundedUnsigned $taskStatus.NativeSpinBlockReason 3
                rollRadiansPerSecond = Get-SpinSpeed $taskStatus.SpinRollSpeed; pitchRadiansPerSecond = Get-SpinSpeed $taskStatus.SpinPitchSpeed
            }
            $taskSample.native = [ordered]@{
                capabilityFlags = Get-Unsigned $taskStatus.NativeCapabilityFlags; inputCoverage = Get-Unsigned $taskStatus.NativeInputCoverage
                menuPath = Get-Unsigned $taskStatus.NativeMenuPath; menuRisingEdges = Get-Unsigned $taskStatus.NativeMenuRisingEdges
                menuPressed = Get-Boolean $taskStatus.NativeMenuPressed; lastInputError = Get-Unsigned $taskStatus.NativeLastInputError
                effectiveActions = Get-Unsigned $taskStatus.NativeEffectiveActions; inputArmed = Get-Boolean $taskStatus.NativeInputArmed
                physicalSamples = Get-Unsigned $taskStatus.PhysicalSamples; routedSamples = Get-Unsigned $taskStatus.RoutedSamples
                genericTrackersAvailable = Get-BoundedUnsigned $taskStatus.NativeGenericTrackerAvailable 64
                genericTrackersSuspended = Get-BoundedUnsigned $taskStatus.NativeGenericTrackerSuspended 64
            }
        } catch {
            ++$taskFailures
            $taskSample.failure = 'BrokerUnavailableOrInvalidReply'
        }
        $taskSample.receivedElapsedMilliseconds = [Math]::Round($taskClock.Elapsed.TotalMilliseconds, 3)
        $taskWriter.WriteLine((ConvertTo-Json -Compress -Depth 8 -InputObject $taskSample))
        ++$taskSamples
        $taskDelay = [int][Math]::Min($IntervalMilliseconds - ($taskClock.Elapsed.TotalMilliseconds - $taskSampleStart), $Seconds * 1000 - $taskClock.Elapsed.TotalMilliseconds)
        if ($taskDelay -gt 0) { Start-Sleep -Milliseconds $taskDelay }
    }
    $taskWriter.WriteLine((ConvertTo-Json -Compress -InputObject ([ordered]@{ type = 'complete'; utc = [DateTime]::UtcNow.ToString('o'); elapsedMilliseconds = [Math]::Round($taskClock.Elapsed.TotalMilliseconds, 3); samples = $taskSamples; failures = $taskFailures })))
} finally { $taskWriter.Dispose() }
Write-Output ('Recorded {0} status samples ({1} unavailable/invalid) to {2}' -f $taskSamples, $taskFailures, $taskLogPath)
