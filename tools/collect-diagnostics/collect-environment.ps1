#Requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../../reports'))

$ErrorActionPreference = 'Stop'
$notes = [System.Collections.Generic.List[string]]::new()
function Redact-Path([string]$Value) {
    if (!$Value) { return $null }
    return $Value.Replace($env:USERPROFILE, '%USERPROFILE%').Replace($env:USERPROFILE.Replace('\','/'), '%USERPROFILE%')
}
function Fingerprint([string]$Value) {
    if (!$Value) { return $null }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
}
function File-Evidence([string]$Path, [string]$Kind) {
    if (!$Path) { return $null }
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $f = Get-Item -LiteralPath $Path
    [ordered]@{ kind=$Kind; path=(Redact-Path $f.FullName); bytes=$f.Length; modifiedUtc=$f.LastWriteTimeUtc.ToString('o'); sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
function Try-Collect([string]$Label, [scriptblock]$Action) {
    try { & $Action } catch { $notes.Add("$Label unavailable: $($_.Exception.GetType().Name)"); return $null }
}

$os = Get-CimInstance Win32_OperatingSystem
$osRegistry = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$report = [ordered]@{
    schemaVersion=1; collectedUtc=[DateTime]::UtcNow.ToString('o'); collector='VRC-SWITCHEROONIE read-only environment inventory';
    privacy=[ordered]@{ usernames='replaced with %USERPROFILE% in paths'; serials='omitted or SHA-256 hashes'; networkAddresses='omitted'; logContents='whitelisted facts only; no messages, world IDs, or command lines'; hardwareIdentifiers='hashed device identities'; accountIdentifiers='not collected' };
    windows=[ordered]@{ caption=$os.Caption; version=$os.Version; build=$os.BuildNumber; updateBuildRevision=$osRegistry.UBR; displayVersion=$osRegistry.DisplayVersion; architecture=$os.OSArchitecture };
    gpuDrivers=@(Get-CimInstance Win32_VideoController | ForEach-Object { [ordered]@{ name=$_.Name; driverVersion=$_.DriverVersion; pnpIdentityHash=(Fingerprint $_.PNPDeviceID) } });
}
Try-Collect 'Native display helper' { if (!('Switcheroonie.Diagnostics.NativeInventory' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'NativeInventory.cs') } } | Out-Null
$report.gpuAdapters = @(Try-Collect 'DXGI GPU adapter/LUID' { [Switcheroonie.Diagnostics.NativeInventory]::Adapters() })
$report.displays = @(Try-Collect 'Display layout/DPI' { [Switcheroonie.Diagnostics.NativeInventory]::Displays() })
$report.displayScaling = [ordered]@{
    note='Effective DPI is process-awareness dependent. Registry percentages below preserve actual per-monitor settings without changing process or global DPI awareness.';
    registry=@(Try-Collect 'Per-monitor scaling' { Get-ChildItem 'HKCU:\Control Panel\Desktop\PerMonitorSettings' -ErrorAction Stop | ForEach-Object { $v=Get-ItemProperty -LiteralPath $_.PSPath; [ordered]@{ monitorKeyHash=(Fingerprint $_.PSChildName); dpiValue=$v.DpiValue; flags=$v.Flags } } });
    desktop=@(Try-Collect 'Desktop DPI settings' { Get-ItemProperty 'HKCU:\Control Panel\Desktop' | Select-Object LogPixels,Win8DpiScaling })
}
$allProcesses = @(Get-Process)
$vrProcesses = @($allProcesses | Where-Object { $_.ProcessName -match 'vrchat|vrserver|vrmonitor|vrcompositor|VirtualDesktop|Pico|Slime|VRCFace|OVR|ALVR|Haritora|SpaceCalib|VRCFT|OSC|Voicemeeter' })
$report.processes = @($vrProcesses | ForEach-Object {
    $start=$null; $path=$null
    try { $start=$_.StartTime.ToUniversalTime().ToString('o') } catch {}
    try { $path=Redact-Path $_.Path } catch {}
    [ordered]@{ name=$_.ProcessName; pid=$_.Id; startUtc=$start; executable=$path; pathReadable=!!$path }
})
$report.steamVrRunning = !!($allProcesses | Where-Object ProcessName -eq 'vrserver')

$steamPath = Try-Collect 'Steam installation registry' { (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam').InstallPath }
$pathsFile = Join-Path $env:LOCALAPPDATA 'openvr/openvrpaths.vrpath'
$vrPaths = Try-Collect 'OpenVR paths' { Get-Content -LiteralPath $pathsFile -Raw | ConvertFrom-Json -AsHashtable }
$runtimePath = $vrPaths.runtime | Select-Object -First 1
$report.runtimePaths = [ordered]@{ steam=(Redact-Path $steamPath); steamVr=(Redact-Path $runtimePath); config=@($vrPaths.config | ForEach-Object {Redact-Path $_}); logs=@($vrPaths.log | ForEach-Object {Redact-Path $_}); pathRegistry=(File-Evidence $pathsFile 'openvr-path-registry') }
$report.openXrRuntime = @(foreach ($key in @('HKLM:\SOFTWARE\Khronos\OpenXR\1','HKLM:\SOFTWARE\WOW6432Node\Khronos\OpenXR\1','HKCU:\SOFTWARE\Khronos\OpenXR\1')) {
    $value=Try-Collect "OpenXR registry $key" { (Get-ItemProperty -LiteralPath $key).ActiveRuntime }
    if ($value) { [ordered]@{ registry=$key; activeRuntime=(Redact-Path $value); note='Registry default is not proof that the current application uses this runtime.' } }
})
$steamManifest = if ($runtimePath) { Join-Path (Split-Path (Split-Path $runtimePath)) 'appmanifest_250820.acf' }
$manifestText = if ($steamManifest -and (Test-Path -LiteralPath $steamManifest)) { Get-Content -LiteralPath $steamManifest -Raw } else { '' }
$build = [regex]::Match($manifestText, '"buildid"\s+"(\d+)"').Groups[1].Value
$channel = [regex]::Match($manifestText, '"BetaKey"\s+"([^"\r\n]+)"').Groups[1].Value
$report.steamVr = [ordered]@{ buildId=$build; channel= $(if ($channel) {$channel} else {'No BetaKey in local manifest; channel not independently proven'}); manifest=(File-Evidence $steamManifest 'steamvr-app-manifest'); versionFile=@(Try-Collect 'SteamVR version files' { Get-ChildItem -LiteralPath $runtimePath -Filter '*version*' -File | ForEach-Object { File-Evidence $_.FullName 'runtime-version-file' } }) }

$report.installedSoftware = @(Try-Collect 'Installed VR software' {
    Get-ItemProperty @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*','HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*') -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -match 'PICO|Virtual Desktop|SteamVR|Steam Link|Slime|VRCFace|Haritora|OpenVR|Oculus|ALVR|Space Calib|Full Body Ratio' } |
    ForEach-Object { [ordered]@{ name=$_.DisplayName; version=$_.DisplayVersion; installLocation=(Redact-Path $_.InstallLocation) } }
})
$driverRoots = @($vrPaths.external_drivers)
if ($runtimePath) { $driverRoots += @(Get-ChildItem -LiteralPath (Join-Path $runtimePath 'drivers') -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName) }
$report.driverManifests = @(foreach ($root in ($driverRoots | Sort-Object -Unique)) {
    $path=Join-Path $root 'driver.vrdrivermanifest'
    if (!(Test-Path -LiteralPath $path)) { $notes.Add("Registered driver manifest missing: $(Redact-Path $root)"); continue }
    $j=Try-Collect 'Driver manifest JSON' { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable }
    $driverBin=Join-Path $root 'bin/win64'
    $binaries=@(if(Test-Path -LiteralPath $driverBin) {
        Get-ChildItem -LiteralPath $driverBin -Filter 'driver_*.dll' -File | ForEach-Object {
            [ordered]@{ name=$_.Name; fileVersion=$_.VersionInfo.FileVersion; productVersion=$_.VersionInfo.ProductVersion; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
        }
    })
    [ordered]@{ root=(Redact-Path $root); name=$j.name; alwaysActivate=$j.alwaysActivate; resourceOnly=$j.resourceOnly; redirectsDisplay=$j.redirectsDisplay; presencePatterns=$j.hmd_presence; binaries=$binaries; evidence=(File-Evidence $path 'driver-manifest') }
})
$settingsPath = $vrPaths.config | Select-Object -First 1 | ForEach-Object {Join-Path $_ 'steamvr.vrsettings'}
$settings=Try-Collect 'SteamVR settings JSON' { Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -AsHashtable }
$report.steamVrSettings = [ordered]@{
    evidence=(File-Evidence $settingsPath 'steamvr-settings'); sections=@($settings.Keys);
    routing=[ordered]@{ activateMultipleDrivers=$settings.steamvr.activateMultipleDrivers; requireHmd=$settings.steamvr.requireHmd; forcedDriver=$settings.steamvr.forcedDriver; forcedHmdHash=(Fingerprint $settings.steamvr.forcedHmd); trackingOverrideCount=$settings.TrackingOverrides.Count };
    driverFlags=@(foreach($key in $settings.Keys | Where-Object {$_ -like 'driver_*'}) { [ordered]@{ section=$key; enable=$settings[$key].enable; blockedBySafeMode=$settings[$key].blocked_by_safe_mode } });
    trackerRoles=@(foreach($key in $settings.trackers.Keys) { [ordered]@{ deviceIdentityHash=(Fingerprint $key); provider=([regex]::Match($key,'^/devices/([^/]+)/').Groups[1].Value); role=$settings.trackers[$key] } });
    trackingOverrides=@(foreach($key in $settings.TrackingOverrides.Keys) { [ordered]@{ sourceHash=(Fingerprint $key); targetHash=(Fingerprint $settings.TrackingOverrides[$key]) } });
    lastKnownHmd=[ordered]@{ driver=$settings.LastKnown.ActualHMDDriver; manufacturer=$settings.LastKnown.HMDManufacturer; model=$settings.LastKnown.HMDModel; serialHash=(Fingerprint $settings.LastKnown.HMDSerialNumber); evidenceScope='Historical stored values; not the current route or current connected device.' };
    applicationBindingReferences=@(foreach($key in $settings.'steam.app.438100'.Keys) {
        [ordered]@{ key=$key; valueHash=(Fingerprint ([string]$settings.'steam.app.438100'[$key])); evidenceScope='Configured VRChat binding reference; raw URL omitted.' }
    });
}
$report.bindingFiles = @(foreach($cfg in $vrPaths.config) {
    $inputRoot=Join-Path $cfg 'input'
    if(Test-Path -LiteralPath $inputRoot) { Get-ChildItem -LiteralPath $inputRoot -Filter '*.json' -File | ForEach-Object { [ordered]@{ kind='binding'; nameHash=(Fingerprint $_.Name); bytes=$_.Length; modifiedUtc=$_.LastWriteTimeUtc.ToString('o'); sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() } } }
})
$bindingCandidates=[System.Collections.Generic.List[object]]::new()
if($steamPath) {
    $defaultsRoot=Join-Path $steamPath 'steamapps/common/VRChat/VRChat_Data/StreamingAssets/SteamVR'
    foreach($file in @('bindings_oculus_touch.json','bindings_vd_hand_controller.json','bindings_knuckles.json')) {
        $path=Join-Path $defaultsRoot $file
        if(Test-Path -LiteralPath $path) {$bindingCandidates.Add([ordered]@{label=$file; path=$path; kind='installed VRChat default'})}
    }
}
foreach($key in $settings.'steam.app.438100'.Keys | Where-Object {$_ -match 'CurrentURL'}) {
    $value=[string]$settings.'steam.app.438100'[$key];$path=$null
    if($value.StartsWith('file:')) { try {$path=([uri]$value).LocalPath} catch {} }
    elseif($value -match '^[A-Za-z]:[\\/]') {$path=$value}
    if($path -and (Test-Path -LiteralPath $path)) {$bindingCandidates.Add([ordered]@{label=$key; path=$path; kind='stored current VRChat binding reference'})}
}
$report.controllerBindingMaps=@(foreach($candidate in $bindingCandidates) {
    $binding=Try-Collect 'Controller action binding JSON' {Get-Content -LiteralPath $candidate.path -Raw | ConvertFrom-Json -AsHashtable}
    $maps=@(foreach($section in $binding.bindings.Keys) {
        foreach($source in $binding.bindings[$section].sources) {
            if($source.path -notmatch '^/user/hand/(left|right)/input/(a|b|x|y|application_menu|system|thumbstick|joystick|trigger|grip)$') {continue}
            foreach($input in $source.inputs.Keys) {
                $output=$source.inputs[$input].output
                if($input -in 'click','position' -and $output -match '^/actions/(global|menu)/in/(menu|quick_menu|main_menu|jump|stick_click|move|confirm|back|drag)$') {
                    [ordered]@{ source=$source.path; component=$input; action=$output }
                }
            }
        }
    })
    [ordered]@{ label=$candidate.label; evidenceScope=$candidate.kind; localPathHash=(Fingerprint $candidate.path); fileSha256=(Get-FileHash -LiteralPath $candidate.path).Hash.ToLowerInvariant(); maps=$maps; limitation='Stored binding only; not proof of active physical controller. Raw metadata and paths omitted.' }
})
$calibrationRoots=@((Join-Path $env:LOCALAPPDATA 'OpenVR-SpaceCalibrator'),(Join-Path $env:APPDATA 'OpenVR-SpaceCalibrator'),(Join-Path $env:LOCALAPPDATA 'SlimeVR'),(Join-Path $env:APPDATA 'SlimeVR'),(Join-Path $env:LOCALAPPDATA 'VRCFaceTracking'),(Join-Path $env:APPDATA 'VRCFaceTracking'))
$report.calibrationFiles = @(foreach($root in $calibrationRoots) {
    if(Test-Path -LiteralPath $root) { Get-ChildItem -LiteralPath $root -File -Recurse -ErrorAction SilentlyContinue | Where-Object {$_.Extension -in '.json','.yaml','.yml','.ini','.toml'} | ForEach-Object { [ordered]@{ root=(Redact-Path $root); relativePathHash=(Fingerprint ([IO.Path]::GetRelativePath($root,$_.FullName))); bytes=$_.Length; modifiedUtc=$_.LastWriteTimeUtc.ToString('o'); sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() } } }
})
$report.faceTrackingModules=@(Try-Collect 'Installed face-tracking module descriptors' {
    $moduleRoot=Join-Path $env:APPDATA 'VRCFaceTracking/CustomLibs'
    if(Test-Path -LiteralPath $moduleRoot) {
        Get-ChildItem -LiteralPath $moduleRoot -Filter 'module.json' -Recurse -File | ForEach-Object {
            $module=Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -AsHashtable
            [ordered]@{ name=$module.ModuleName; version=$module.Version; descriptorHash=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant(); evidenceScope='Installed descriptor; not proof of runtime activity or hardware.' }
        }
    }
})
$oscRoot=Join-Path $env:USERPROFILE 'AppData/LocalLow/VRChat/VRChat/OSC'
$report.oscConfiguration=[ordered]@{ present=(Test-Path -LiteralPath $oscRoot); accountDirectoryCount=@(Get-ChildItem -LiteralPath $oscRoot -Directory -ErrorAction SilentlyContinue).Count; contents='Not exported; profile paths contain account/avatar identifiers.' }
$report.localDevices = @(Try-Collect 'VR/trackers/audio PnP inventory' {
    Get-CimInstance Win32_PnPEntity | Where-Object { $_.Name -match 'PICO|Quest|Oculus|Valve VR|Vive|Index|Haritora|Slime|PS VR|PlayStation VR|VRCFace' } |
    ForEach-Object { [ordered]@{ name=$_.Name; class=$_.PNPClass; status=$_.Status; identityHash=(Fingerprint $_.DeviceID) } }
})
$report.audioEndpoints = @(Try-Collect 'Audio endpoint inventory' {
    Get-CimInstance Win32_PnPEntity | Where-Object PNPClass -eq 'AudioEndpoint' | ForEach-Object { [ordered]@{ name=$_.Name; status=$_.Status; identityHash=(Fingerprint $_.DeviceID) } }
})
$report.oscEndpoints = @(Try-Collect 'UDP OSC/VR endpoint inventory' {
    Get-NetUDPEndpoint | Where-Object { $_.LocalPort -in 9000,9001,9002,9003,9451,9452,39570 -or $_.OwningProcess -in $vrProcesses.Id } |
    ForEach-Object { [ordered]@{ localPort=$_.LocalPort; ownerPid=$_.OwningProcess; ownerName=($allProcesses | Where-Object Id -eq $_.OwningProcess | Select-Object -First 1).ProcessName } }
})

$currentApp = $allProcesses | Where-Object ProcessName -eq 'VRChat' | Select-Object -First 1
$moduleNames=@(Try-Collect 'VRChat XR modules (may be protected)' { $currentApp.Modules | Where-Object ModuleName -match 'openvr|openxr|vdxr|oculus|UnityPlayer|GameAssembly' | ForEach-Object ModuleName })
if($currentApp -and !$moduleNames.Count) { $notes.Add('No relevant VRChat modules were readable. Protected process/module access is not evidence of a missing XR loader.') }
$logRoot=Join-Path $env:USERPROFILE 'AppData/LocalLow/VRChat/VRChat'
$logFile=Try-Collect 'VRChat latest log' { Get-ChildItem -LiteralPath $logRoot -Filter 'output_log*' -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
$logFacts=[ordered]@{ selection='Most recently modified output_log; association with running process inferred by start time, not established by PID in file.'; modifiedUtc=$null; logIdentityHash=$null; xrDevice=$null; unityVersion=$null; applicationVersion=$null; steamVrErrorCodes=@(); steamVrInitializationFailed=$false; microphone=$null; startupAudioOutput=$null }
if($logFile) {
    $logFacts.modifiedUtc=$logFile.LastWriteTimeUtc.ToString('o'); $logFacts.logIdentityHash=Fingerprint $logFile.Name
    # Never export raw lines: VRChat logs contain account, world, instance, conversation, and authentication data.
    $head=Get-Content -LiteralPath $logFile.FullName -TotalCount 1600
    foreach($line in $head) {
        if($line -match '^\s*XR Device:\s*(.{1,100})$') { $logFacts.xrDevice=$Matches[1].Trim() }
        if($line -match '^\s*Unity Version:\s*([A-Za-z0-9.\-]+)$') { $logFacts.unityVersion=$Matches[1] }
        if($line -match 'ProductVersion=(w_[A-Za-z0-9.\-]+)') { $logFacts.applicationVersion=$Matches[1] }
        if($line -match '\[SteamVR\]</b> Not Initialized \((\d+)\)') { $logFacts.steamVrErrorCodes += [int]$Matches[1] }
        if($line -match '\[SteamVR\]</b> Initialization failed') { $logFacts.steamVrInitializationFailed=$true }
        if($line -match "Microphone device changing to '([^']{1,180})'") { $logFacts.microphone=$Matches[1] }
    }
    $logFacts.steamVrErrorCodes=@($logFacts.steamVrErrorCodes | Sort-Object -Unique)
}
$runtimeAssessment = if(!$currentApp) { 'VRChat is not running. No actual application runtime can be established.' } elseif($logFacts.xrDevice -eq 'None' -and !$report.steamVrRunning) { 'Current VRChat log reports XR Device: None; SteamVR server absent. Evidence supports a desktop/no-XR session, not a switch-ready SteamVR session.' } elseif($moduleNames -contains 'openvr_api.dll' -and $report.steamVrRunning) { 'OpenVR module and SteamVR server present; confirm active device/scene PID with the native probe before treating route as proven.' } else { 'Runtime remains unproven. Installed transports and the OpenXR registry default do not establish the running application route.' }
$report.targetApplication=[ordered]@{ name='VRChat'; running=!!$currentApp; pid=$currentApp.Id; xrModuleNames=$moduleNames; startupLogFacts=$logFacts; runtimeAssessment=$runtimeAssessment }
$report.unproven=@('Current HMD/provider/controller identities and pre-override physical pose stream require a connected runtime probe.','PICO model/firmware and Enterprise transport compatibility are not proven by installed software.','Headset-free startup with later stereo connection has not been hardware tested.','Per-application Windows audio output routing/default roles are not collected; no device settings were changed.','Existing binding/calibration fingerprints establish stored configuration, not live calibration correctness.','No physical switching, tracker calibration, microphone, menu, or streaming tests were performed by this collector.')
$report.collectionNotes=@($notes)

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$jsonPath=Join-Path $OutputDirectory 'environment-report.json'
$mdPath=Join-Path $OutputDirectory 'environment-report.md'
$report | ConvertTo-Json -Depth 18 | Set-Content -LiteralPath $jsonPath -Encoding utf8
$text=[System.Collections.Generic.List[string]]::new()
$text.Add('# VRC-SWITCHEROONIE environment inventory')
$text.Add('')
$text.Add("Collected UTC: $($report.collectedUtc). Read-only: no runtime launch, driver registration, binding rewrite, audio switch, or restart.")
$text.Add('')
$text.Add("**Actual application evidence:** $runtimeAssessment")
$text.Add('')
$text.Add("Windows: $($os.Caption) $($os.Version).$($osRegistry.UBR), $($os.OSArchitecture). SteamVR installed build: $build. SteamVR server running: $($report.steamVrRunning).")
$text.Add('')
$text.Add('## GPU and display layout')
$text.Add('')
foreach($gpu in $report.gpuAdapters) { $text.Add("- $($gpu.Name), LUID $($gpu.Luid), dedicated VRAM $([math]::Round($gpu.DedicatedVideoBytes/1GB,2)) GiB.") }
foreach($display in $report.displays) { $text.Add("- $($display.Device): $($display.Width) x $($display.Height) at ($($display.X), $($display.Y)); primary=$($display.Primary); effective DPI=$($display.DpiX) x $($display.DpiY).") }
$text.Add('')
$text.Add('## Installed transports and tracking tools')
$text.Add('')
foreach($app in $report.installedSoftware) { $text.Add("- $($app.name): $(if($app.version){$app.version}else{'version not reported'}).") }
$text.Add('')
$text.Add('## Existing ecosystem')
$text.Add('')
$text.Add("SteamVR settings: activateMultipleDrivers=$($settings.steamvr.activateMultipleDrivers), requireHmd=$($settings.steamvr.requireHmd). Tracking overrides: $($settings.TrackingOverrides.Count).")
$text.Add("Stored tracker role records: $($report.steamVrSettings.trackerRoles.Count). Providers: $(($report.steamVrSettings.trackerRoles.provider | Sort-Object -Unique) -join ', ').")
$text.Add("Binding file fingerprints: $($report.bindingFiles.Count). Calibration/face-tracking file fingerprints: $($report.calibrationFiles.Count).")
$text.Add("Registered/installed driver names: $(($report.driverManifests.name | Sort-Object -Unique) -join ', ').")
$text.Add("Historical SteamVR LastKnown HMD: $($settings.LastKnown.HMDModel), driver $($settings.LastKnown.ActualHMDDriver). This does not establish the current headset.")
$text.Add("Configured VRChat binding reference fingerprints: $($report.steamVrSettings.applicationBindingReferences.Count). Face-tracking module descriptors: $(($report.faceTrackingModules | ForEach-Object {'{0} {1}' -f $_.name,$_.version}) -join ', ').")
$text.Add("Read-only controller action maps: $($report.controllerBindingMaps.Count) files; see JSON for current/default menu, jump, movement, confirm, and drag sources. No controller binding was modified.")
$text.Add("Running relevant processes: $(($report.processes.name | Sort-Object -Unique) -join ', ').")
if($logFacts.microphone) { $text.Add("VRChat startup microphone from local log: $($logFacts.microphone). This is a startup selection, not proof of current audio routing.") }
$text.Add('')
$text.Add('## Not yet proven')
$text.Add('')
foreach($item in $report.unproven) {$text.Add("- $item")}
$text.Add('')
$text.Add('## Collection limitations')
$text.Add('')
foreach($item in $notes) {$text.Add("- $item")}
$text.Add('')
$text.Add('Reports omit raw serials, local usernames, network addresses, command lines, account identifiers, and raw application logs. The JSON provides redacted paths and file fingerprints for reproducible preservation checks.')
$text | Set-Content -LiteralPath $mdPath -Encoding utf8
Write-Output "Wrote redacted environment-report.json and environment-report.md. Runtime assessment: $runtimeAssessment"
