param([switch]$SkipTests, [switch]$FrameworkDependent, [string]$PackageName = 'VRC-SWITCHEROONIE-0.2.5',
    [string]$NativeBuildDirectory = 'build/native-release-0.2.5')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$nativeBuild = [IO.Path]::GetFullPath((Join-Path $repo $NativeBuildDirectory))
$ownedBuild = [IO.Path]::GetFullPath((Join-Path $repo 'build')) + '\'
if (!$nativeBuild.StartsWith($ownedBuild, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'NativeBuildDirectory must be a separate directory inside this workspace build folder.'
}
if ($PackageName -notmatch '^VRC-SWITCHEROONIE-[0-9]+\.[0-9]+\.[0-9]+(?:-[a-z0-9-]+)?$') { throw 'Invalid owned package name.' }
$package = Join-Path (Join-Path $repo 'dist') $PackageName
$managedPublish = [IO.Path]::GetFullPath((Join-Path $repo ("build\managed-publish-" + $PackageName)))
if (!$managedPublish.StartsWith($ownedBuild, [StringComparison]::OrdinalIgnoreCase)) { throw 'Managed publish artifacts must stay inside this workspace build folder.' }
if (Test-Path -LiteralPath $managedPublish) { throw 'Managed publication requires a new isolated artifacts directory. Use a new PackageName.' }
if (Test-Path -LiteralPath $package) {
    throw 'Portable package directories are immutable. Use a new PackageName for staging; existing packages are preserved.'
}
New-Item -ItemType Directory -Force -Path (Join-Path $repo 'reports') | Out-Null
& (Join-Path $PSScriptRoot 'Fetch-Dependencies.ps1')
function Run-Native([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe failed ($LASTEXITCODE)" }
}
function Run-Suite([string]$Name,[string]$Exe,[string[]]$Arguments) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $output = & $Exe @Arguments 2>&1
    $code = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($code -ne 0) { throw "$Name failed ($code)" }
    $status = 'passed'
    $notRun = $null
    if ($Name -eq 'Actual broker/helper process integration') {
        $integrationPath = Join-Path $repo 'reports\process-integration.json'
        if (!(Test-Path -LiteralPath $integrationPath)) { throw 'Process integration did not produce its report.' }
        $integration = Get-Content -LiteralPath $integrationPath -Raw | ConvertFrom-Json
        $notRun = @($integration.results | Where-Object {$_.status -eq 'NotRun'}).Count
        if ($notRun -gt 0) { $status = 'partial_not_run' }
    }
    return [pscustomobject]@{name=$Name;status=$status;exitCode=$code;elapsedMilliseconds=$watch.Elapsed.TotalMilliseconds;notRun=$notRun}
}
Run-Native 'cmake' @('-S',$repo,'-B',$nativeBuild,'-G','Visual Studio 17 2022','-A','x64')
Run-Native 'cmake' @('--build',$nativeBuild,'--config','Release','--parallel','4')
if (-not $SkipTests) {
    $suites = @()
    $suites += Run-Suite 'Native routing, isolated hooks, controlled-scene shader/matrix self-test' 'ctest' @('--test-dir',$nativeBuild,'-C','Release','--output-on-failure')
    $suites += Run-Suite 'Managed transactions and real OSC encoding/release' 'dotnet' @('run','--project',(Join-Path $repo 'tests\Switcheroonie.Tests\Switcheroonie.Tests.csproj'),'-c','Release')
    $suites += Run-Suite 'Compact resident panel and owned sign-in startup fixtures' 'dotnet' @('run','--project',(Join-Path $repo 'research\compact-ui\CompactUi.csproj'),'-c','Release','--','--self-test-compact')
    $suites += Run-Suite 'Actual broker/helper process integration' 'dotnet' @('run','--project',(Join-Path $repo 'tests\IntegrationHarness\IntegrationHarness.csproj'),'-c','Release')
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $suites += Run-Suite 'Windows PowerShell installer journal recovery' $windowsPowerShell @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'tests\DriverJournal\Test-PowerShell51.ps1'))
    $suites += Run-Suite 'Owned installer relocation and rollback' $windowsPowerShell @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'tests\DriverJournal\Test-Relocation.ps1'))
    $suites += Run-Suite 'Canonical profile configuration and installer ownership' $windowsPowerShell @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'tests\DriverJournal\Test-CanonicalState.ps1'))
    $suites += Run-Suite 'Installer runtime guards and exclusive first-run setup' $windowsPowerShell @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'tests\DriverJournal\Test-ProcessGuard.ps1'))
    $suites += Run-Suite 'Standalone archive complete payload and tamper rejection' $windowsPowerShell @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'tests\StandaloneArchive\Test-Archive.ps1'))
    $suites += Run-Suite 'Signed portable updater malicious archive and rollback fixtures' 'dotnet' @('run','--project',(Join-Path $repo 'tests\Switcheroonie.Update.Tests\Switcheroonie.Update.Tests.csproj'),'-c','Release')
    [pscustomobject]@{schemaVersion=1;dateUtc=[DateTime]::UtcNow.ToString('o');category='Software automated + isolated simulated runtime; no hardware certification';suites=$suites;physicalTests='Not run for this candidate';coldStart='Resident service implemented; persistent-HMD VD trials failed and were rolled back; graphics bridge unimplemented';runtimeMutationsDuringTests=$false} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $repo 'reports\software-validation.json') -Encoding UTF8
}
New-Item -ItemType Directory -Force -Path $package | Out-Null
foreach ($project in 'UI','Broker','Cli','Update') {
    $path = if ($project -eq 'UI') { Join-Path $repo 'research\compact-ui\CompactUi.csproj' } else {
        Join-Path $repo "src\Switcheroonie.$project\Switcheroonie.$project.csproj"
    }
    $publishArgs = @('publish',$path,'-c','Release','-r','win-x64','--artifacts-path',$managedPublish,'-o',$package,'--self-contained',(-not $FrameworkDependent).ToString().ToLowerInvariant(),'-p:PublishSingleFile=false','-p:PublishReadyToRun=false','-p:DebugType=None','-p:DebugSymbols=false')
    Run-Native 'dotnet' $publishArgs
}
Copy-Item -LiteralPath (Join-Path $nativeBuild 'driver\switcheroonie') -Destination (Join-Path $package 'driver') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repo 'native\approved-companion.json') -Destination (Join-Path $package 'driver\approved-companion.json') -Force
Copy-Item -LiteralPath (Join-Path $nativeBuild 'Release\switcheroonie-diagnostics.exe'),(Join-Path $nativeBuild 'Release\switcheroonie-test-scene.exe'),(Join-Path $nativeBuild 'Release\openvr_api.dll') -Destination $package -Force
foreach ($folder in 'docs','profiles') {
    Copy-Item -LiteralPath (Join-Path $repo $folder) -Destination $package -Recurse -Force
}
foreach ($excluded in 'build-specification.reference.md') {
    $excludedPath = Join-Path (Join-Path $package 'docs') $excluded
    if (Test-Path -LiteralPath $excludedPath) { Remove-Item -LiteralPath $excludedPath }
}
New-Item -ItemType Directory -Force -Path (Join-Path $package 'tools'),(Join-Path $package 'third_party') | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'tools\Manage-Driver.ps1') -Destination (Join-Path $package 'tools') -Force
Copy-Item -LiteralPath (Join-Path $repo 'tools\Test-ConnectedSwitching.ps1') -Destination (Join-Path $package 'tools') -Force
Copy-Item -LiteralPath (Join-Path $repo 'tools\Record-DirectGameInput.ps1') -Destination (Join-Path $package 'tools') -Force
Copy-Item -LiteralPath (Join-Path $repo 'tools\collect-diagnostics') -Destination (Join-Path $package 'tools') -Recurse -Force
$ownedPackagePrefix = [IO.Path]::GetFullPath($package) + '\'
foreach ($cache in @(Get-ChildItem -LiteralPath (Join-Path $package 'tools') -Directory -Recurse -Force | Where-Object {$_.Name -eq '__pycache__'})) {
    $cachePath = [IO.Path]::GetFullPath($cache.FullName)
    if (!$cachePath.StartsWith($ownedPackagePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Cache cleanup target escaped staged package.' }
    Remove-Item -LiteralPath $cachePath -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE'),(Join-Path $repo 'THIRD_PARTY_NOTICES.md'),(Join-Path $repo 'README.md') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $repo 'third_party\openvr\LICENSE') -Destination (Join-Path $package 'third_party\OpenVR-LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $repo 'third_party\minhook\LICENSE.txt') -Destination (Join-Path $package 'third_party\MinHook-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $package 'Switcheroonie.Updater.exe') -Destination (Join-Path $package 'VRC-SWITCHEROONIE.exe') -Force
Set-Content -LiteralPath (Join-Path $package 'VRC-SWITCHEROONIE.cmd') -Value '@echo off
start "" "%~dp0VRC-SWITCHEROONIE.exe" --launch' -Encoding ASCII
$launcherBuild = Join-Path $repo ("build\standalone-launcher-" + $PackageName)
$innerHash = (Get-FileHash -LiteralPath (Join-Path $package 'VRC-SWITCHEROONIE.exe') -Algorithm SHA256).Hash
New-Item -ItemType Directory -Force -Path $launcherBuild | Out-Null
$launcherInventory = Join-Path $launcherBuild 'payload-inventory.txt'
Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($package.Length + 1).Replace('\','/')
    if ($relative.Contains('|') -or $relative.Contains("`n") -or $relative.Contains("`r")) { throw 'Invalid launcher inventory path.' }
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '|' + $relative
} | Set-Content -LiteralPath $launcherInventory -Encoding UTF8
Run-Native 'cmake' @('-S',(Join-Path $repo 'native\launcher'),'-B',$launcherBuild,'-G','Visual Studio 17 2022','-A','x64',('-DSWITCHEROONIE_INNER_SHA256=' + $innerHash),('-DSWITCHEROONIE_PAYLOAD_INVENTORY=' + $launcherInventory))
Run-Native 'cmake' @('--build',$launcherBuild,'--config','Release','--parallel','2')
New-Item -ItemType Directory -Path (Join-Path $package 'portable') | Out-Null
Copy-Item -LiteralPath (Join-Path $launcherBuild 'Release\VRC-SWITCHEROONIE.exe') -Destination (Join-Path $package 'portable\VRC-SWITCHEROONIE.exe')
if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot '..\tests\StandaloneLauncher\Test-Launcher.ps1') -LaunchFixture
    if ($LASTEXITCODE -ne 0) { throw 'Standalone launcher fixtures failed.' }
}
$hashes = Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object {
    [pscustomobject]@{path=$_.FullName.Substring($package.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$hashes | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $package 'package-hashes.json') -Encoding UTF8
$zip = Join-Path (Join-Path $repo 'dist') ($PackageName + '-win-x64.zip')
Compress-Archive -LiteralPath $package -DestinationPath $zip -Force
$portableZip = Join-Path (Join-Path $repo 'dist') ($PackageName + '-portable.zip')
& (Join-Path $PSScriptRoot 'New-StandaloneArchive.ps1') -Package $package -Output $portableZip
Write-Host "Package: $package"
Write-Host "Archive: $zip"
Write-Host "Standalone download: $portableZip"
