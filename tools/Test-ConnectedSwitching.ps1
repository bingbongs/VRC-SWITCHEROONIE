param(
    [Parameter(Mandatory=$true)][int]$SceneProcessId,
    [Parameter(Mandatory=$true)][string]$SceneLog,
    [ValidateRange(1,100)][int]$Cycles = 25,
    [string]$Output = (Join-Path $env:LOCALAPPDATA 'VRC-SWITCHEROONIE\connected-switching.json')
)
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Directory]::GetParent($PSScriptRoot).FullName
$cli = Join-Path $packageRoot 'Switcheroonie.Cli.exe'
if (!(Test-Path -LiteralPath $cli)) { throw 'Run this script from the built package tools directory.' }
$logPath = [IO.Path]::GetFullPath($SceneLog)
function Assert-ControlledScene {
    $scene = Get-Process -Id $SceneProcessId -ErrorAction Stop
    if ($scene.ProcessName -ne 'switcheroonie-test-scene' -or @(Get-Process VRChat -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'Only the controlled test scene may be active for this automated switching test.'
    }
    if ($scene.StartTime.ToUniversalTime() -ne $script:sceneStart) { throw 'The scene process identity changed.' }
}
function Invoke-Broker([string]$Verb) {
    $lines = & $cli $Verb
    if ($LASTEXITCODE -ne 0) { throw "Broker $Verb failed: $($lines -join ' ')" }
    $reply = ($lines -join "`n") | ConvertFrom-Json
    if (!$reply.Accepted) { throw "Broker $Verb was rejected." }
    return $reply
}
function Read-Frame([uint64]$After = 0, [int]$ExpectedMode = -1, [long]$Epoch = 0) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $frame = Get-Content -LiteralPath $logPath -Tail 12 | ForEach-Object {
            try { $row = $_ | ConvertFrom-Json; if ($row.event -eq 'frame') { $row } } catch { }
        } | Select-Object -Last 1
        if ($null -ne $frame -and $frame.processId -eq $SceneProcessId -and $frame.frame -gt $After -and ($ExpectedMode -lt 0 -or ($frame.driver.mode -eq $ExpectedMode -and $frame.driver.epoch -ge $Epoch))) { break }
        if ($deadline.ElapsedMilliseconds -ge 2000) { throw 'No advancing frame from the controlled scene log within two seconds.' }
        Start-Sleep -Milliseconds 100
    } while ($true)
    if ($frame.leftSubmitError -ne 0 -or $frame.rightSubmitError -ne 0 -or !$frame.headValid -or !$frame.headConnected -or !$frame.driver.statusAlive -or !$frame.driver.capturedPhysicalHeadAvailable) {
        throw 'Compositor submission or independent driver capture is unavailable.'
    }
    return $frame
}
$sceneStart = (Get-Process -Id $SceneProcessId -ErrorAction Stop).StartTime.ToUniversalTime()
Assert-ControlledScene
$initial = Invoke-Broker 'status'
if (!$initial.Status.DriverAlive -or !$initial.Status.HasHead -or ($initial.Status.NativeCapabilityFlags -band 1) -eq 0) {
    throw 'A live pose hook and fresh physical head capture are required.'
}
$null = Invoke-Broker 'release'
$before = Read-Frame
$results = [Collections.Generic.List[object]]::new()
$failure = $null
try {
    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        foreach ($mode in 'Desktop','Physical') {
            Assert-ControlledScene
            $timer = [Diagnostics.Stopwatch]::StartNew()
            $reply = Invoke-Broker $mode.ToLowerInvariant()
            if ($reply.Status.Mode -ne $mode -or !$reply.Status.DriverAlive -or !$reply.Status.HasHead) { throw 'The native committed mode or physical capture did not match the request.' }
            Start-Sleep -Milliseconds 200
            $expectedMode = if ($mode -eq 'Desktop') { 1 } else { 0 }
            $frame = Read-Frame $before.frame $expectedMode $reply.Status.Epoch
            if ($frame.frame -le $before.frame) { throw 'Controlled-scene frame counter stopped advancing.' }
            $results.Add([pscustomobject]@{cycle=$cycle;mode=$mode;epoch=$reply.Status.Epoch;transactionMilliseconds=$timer.Elapsed.TotalMilliseconds;sceneFrame=$frame.frame;sceneElapsedSeconds=$frame.elapsedSeconds;headAgeMilliseconds=$reply.Status.HeadAgeMilliseconds})
            $before = $frame
        }
        if ($cycle % 5 -eq 0) { Write-Host "$cycle/$Cycles controlled scene cycles acknowledged; frame $($before.frame)." }
    }
} catch { $failure = $_.Exception.Message }
finally {
    try { $null = Invoke-Broker 'release'; $null = Invoke-Broker 'physical' } catch { if ($null -eq $failure) { $failure = $_.Exception.Message } }
    $report = [ordered]@{schemaVersion=1;dateUtc=[DateTime]::UtcNow.ToString('o');evidence='Live controlled scene and native acknowledgements';automaticChecksPassed=($null -eq $failure);failure=$failure;sceneProcessId=$SceneProcessId;requestedCycles=$Cycles;completedTransitions=$results.Count;transitions=@($results.ToArray());headsetPresentation='Requires human observation';physicalSourceIndependenceDuringHeadMotion='Requires a separate real-motion F1 experiment';VRChatMenusMovementAndMultiplayer='Not tested by this scene';coldStart='Unimplemented'}
    $outputPath = [IO.Path]::GetFullPath($Output)
    New-Item -ItemType Directory -Path ([IO.Directory]::GetParent($outputPath).FullName) -Force | Out-Null
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $outputPath -Encoding UTF8
}
if ($null -ne $failure) { throw "Controlled switching failed: $failure. Local report: $outputPath" }
Write-Host "Native acknowledgements and continuing scene frames passed for $Cycles cycles. Headset presentation and VRChat behavior remain separate human tests."
