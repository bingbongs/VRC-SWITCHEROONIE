param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot '../..'),
    [string]$BuildRoot = '',
    [switch]$LaunchFixture
)
$ErrorActionPreference = 'Stop'
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$allowedBuild = [IO.Path]::GetFullPath((Join-Path $SourceRoot 'build'))
if (-not $BuildRoot) { $BuildRoot = Join-Path $allowedBuild 'standalone-launcher-tests' }
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
if (-not $BuildRoot.StartsWith($allowedBuild.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fixture output must remain inside this workspace build directory.'
}
New-Item -ItemType Directory -Path $BuildRoot -Force | Out-Null
$fixtureBuild = Join-Path $BuildRoot 'fixture-build'
$launcherBuild = Join-Path $BuildRoot 'launcher-build'
& cmake -S (Join-Path $SourceRoot 'tests/StandaloneLauncher') -B $fixtureBuild -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE -ne 0) { throw 'Fixture configure failed.' }
& cmake --build $fixtureBuild --config Release
if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
$payload = Join-Path $fixtureBuild 'Release/launcher-fixture-payload.exe'
$payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
$payloadSource = Join-Path $BuildRoot 'immutable-fixture-payload'
New-Item -ItemType Directory -Path $payloadSource -Force | Out-Null
Copy-Item -LiteralPath $payload -Destination (Join-Path $payloadSource 'VRC-SWITCHEROONIE.exe') -Force
[IO.File]::WriteAllText((Join-Path $payloadSource 'fixture-runtime.dll'), 'Harmless pinned dependency fixture; never loaded.')
$localeName = 'locale-' + [char]0x03A9
New-Item -ItemType Directory -Path (Join-Path $payloadSource $localeName) -Force | Out-Null
[IO.File]::WriteAllText((Join-Path (Join-Path $payloadSource $localeName) 'resources.dll'), 'Harmless Unicode resource fixture; never loaded.')
$inventory = Join-Path $BuildRoot 'payload-inventory.txt'
Get-ChildItem -LiteralPath $payloadSource -File -Recurse | Sort-Object FullName | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '|' + $_.FullName.Substring($payloadSource.Length+1).Replace('\','/')
} | Set-Content -LiteralPath $inventory -Encoding UTF8
& cmake -S (Join-Path $SourceRoot 'native/launcher') -B $launcherBuild -G 'Visual Studio 17 2022' -A x64 "-DSWITCHEROONIE_INNER_SHA256=$payloadHash" "-DSWITCHEROONIE_PAYLOAD_INVENTORY=$inventory"
if ($LASTEXITCODE -ne 0) { throw 'Launcher configure failed.' }
& cmake --build $launcherBuild --config Release
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
$wrapper = Join-Path $launcherBuild 'Release/VRC-SWITCHEROONIE.exe'
$casesRoot = Join-Path $BuildRoot ('cases-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $casesRoot | Out-Null
$script:checks = 0
$script:failures = 0
$script:skipped = @()
function Check([bool]$condition, [string]$name) {
    $script:checks++
    if (-not $condition) { $script:failures++; Write-Output "FAIL $name" }
}
function New-Case([string]$name, [bool]$withApp = $true, [bool]$withInner = $true) {
    $root = Join-Path $casesRoot $name
    New-Item -ItemType Directory -Path $root | Out-Null
    Copy-Item -LiteralPath $wrapper -Destination (Join-Path $root 'VRC-SWITCHEROONIE.exe')
    if ($withApp) {
        New-Item -ItemType Directory -Path (Join-Path $root 'App') | Out-Null
        Copy-Item -LiteralPath (Join-Path $payloadSource 'fixture-runtime.dll'),(Join-Path $payloadSource $localeName) -Destination (Join-Path $root 'App') -Recurse
        if ($withInner) { Copy-Item -LiteralPath $payload -Destination (Join-Path $root 'App/VRC-SWITCHEROONIE.exe') }
        New-Item -ItemType Directory -Path (Join-Path $root 'App/portable') | Out-Null
        Copy-Item -LiteralPath $wrapper -Destination (Join-Path $root 'App/portable/VRC-SWITCHEROONIE.exe')
        [IO.File]::WriteAllText((Join-Path $root 'App/package-hashes.json'), '{}')
    }
    return $root
}
function Invoke-Wrapper([string]$root, [string[]]$arguments = @('--verify-only')) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $root 'VRC-SWITCHEROONIE.exe'
    $info.Arguments = $arguments -join ' '
    $info.WorkingDirectory = $root
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit(5000)) {
            # This is exclusively the captured child created above from our
            # fixture wrapper. Never enumerate or terminate user processes.
            $process.Kill(); $process.WaitForExit()
            throw 'Owned fixture wrapper exceeded its five-second deadline.'
        }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function No-Child([string]$root) { return -not (Test-Path -LiteralPath (Join-Path $root 'App/fixture-result.txt')) }
$correct = New-Case 'correct'
Check ((Invoke-Wrapper $correct) -eq 0) 'exact pinned payload passes silent verification'
Check (No-Child $correct) 'verify-only never starts even the harmless fixture payload'
$missingApp = New-Case 'missing-app' $false $false
Check ((Invoke-Wrapper $missingApp) -eq 1) 'missing App folder is rejected silently'
$missingExe = New-Case 'missing-inner' $true $false
Check ((Invoke-Wrapper $missingExe) -eq 1) 'missing inner launcher is rejected silently'
$tampered = New-Case 'tampered'
$bytes = [IO.File]::ReadAllBytes((Join-Path $tampered 'App/VRC-SWITCHEROONIE.exe'))
$bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
[IO.File]::WriteAllBytes((Join-Path $tampered 'App/VRC-SWITCHEROONIE.exe'), $bytes)
Check ((Invoke-Wrapper $tampered) -eq 1) 'one changed byte fails the CNG SHA256 pin'
Check (No-Child $tampered) 'tampered bytes are never launched'
$dependency = New-Case 'tampered-dependency'
[IO.File]::WriteAllText((Join-Path $dependency 'App/fixture-runtime.dll'), 'Changed dependency with unchanged launcher.')
Check ((Invoke-Wrapper $dependency) -eq 1) 'changed dependency is rejected while inner EXE bytes still match'
Check (No-Child $dependency) 'tampered dependency cannot reach even the harmless child'
$missingDependency = New-Case 'missing-dependency'
Remove-Item -LiteralPath (Join-Path $missingDependency 'App/fixture-runtime.dll')
Check ((Invoke-Wrapper $missingDependency) -eq 1) 'missing pinned dependency is rejected'
$extraDependency = New-Case 'extra-dll'
[IO.File]::WriteAllText((Join-Path $extraDependency 'App/extra.dll'), 'Unlisted file; never loaded.')
Check ((Invoke-Wrapper $extraDependency) -eq 1) 'unexpected DLL outside the immutable inventory is rejected'
$badMirror = New-Case 'changed-portable-mirror'
[IO.File]::WriteAllText((Join-Path $badMirror 'App/portable/VRC-SWITCHEROONIE.exe'), 'Not this wrapper.')
Check ((Invoke-Wrapper $badMirror) -eq 1) 'portable mirror must hash-match the running root wrapper'
$missingMirror = New-Case 'missing-portable-mirror'
Remove-Item -LiteralPath (Join-Path $missingMirror 'App/portable/VRC-SWITCHEROONIE.exe')
Check ((Invoke-Wrapper $missingMirror) -eq 1) 'production portable mirror is required'
$oversizeMetadata = New-Case 'oversize-metadata'
[IO.File]::WriteAllBytes((Join-Path $oversizeMetadata 'App/package-hashes.json'), (New-Object byte[] (2*1024*1024+1)))
Check ((Invoke-Wrapper $oversizeMetadata) -eq 1) 'nonexecuted package metadata remains bounded to two MiB'
$writeLocked = New-Case 'write-locked-dependency'
$writing = [IO.File]::Open((Join-Path $writeLocked 'App/fixture-runtime.dll'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
try { Check ((Invoke-Wrapper $writeLocked) -eq 1) 'a concurrent in-place dependency writer blocks verification' }
finally { $writing.Dispose() }
Check ((Invoke-Wrapper $writeLocked) -eq 0) 'ordinary complete payload verifies after the competing writer closes'
$empty = New-Case 'empty'
[IO.File]::WriteAllBytes((Join-Path $empty 'App/VRC-SWITCHEROONIE.exe'), [byte[]]@())
Check ((Invoke-Wrapper $empty) -eq 1) 'empty payload is rejected'
$asDirectory = New-Case 'exe-is-directory' $true $false
New-Item -ItemType Directory -Path (Join-Path $asDirectory 'App/VRC-SWITCHEROONIE.exe') | Out-Null
Check ((Invoke-Wrapper $asDirectory) -eq 1) 'directory disguised as an executable is rejected'
$unicode = New-Case ('space ' + [char]0x03A9 + ' ' + [char]0x96EA)
Check ((Invoke-Wrapper $unicode) -eq 0) 'absolute UTF16 paths with spaces and Unicode verify correctly'
foreach ($badArguments in @(
    @('--verify-only', '--unknown'),
    @('--verify-only', '--background'),
    @('--verify-only', '--verify-only'),
    @('--verify-only', '--BACKGROUND')
)) {
    Check ((Invoke-Wrapper $correct $badArguments) -eq 1) 'only exact standalone argument forms are admitted'
}
Check (No-Child $correct) 'invalid argument combinations start no child'
$junction = New-Case 'app-junction' $false $false
New-Item -ItemType Junction -Path (Join-Path $junction 'App') -Target (Join-Path $correct 'App') | Out-Null
Check ((Invoke-Wrapper $junction) -eq 1) 'App junction is rejected despite matching target bytes'
if (-not ('StandaloneFixtureLink' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class StandaloneFixtureLink {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CreateSymbolicLink(string link, string target, uint flags);
}
'@
}
$fileLink = New-Case 'inner-file-link' $true $false
if ([StandaloneFixtureLink]::CreateSymbolicLink((Join-Path $fileLink 'App/VRC-SWITCHEROONIE.exe'), $payload, 2)) {
    Check ((Invoke-Wrapper $fileLink) -eq 1) 'inner executable symbolic link is rejected'
} else {
    $script:skipped += ('inner-file-link: OS refused fixture symlink creation, error ' + [Runtime.InteropServices.Marshal]::GetLastWin32Error())
}
if ($LaunchFixture) {
    $heldRoot = New-Case 'held-bootstrap'
    $holdMarker = Join-Path $heldRoot 'fixture-hold.flag'
    [IO.File]::WriteAllText($holdMarker, 'Harmless fixture-only bootstrap barrier')
    $heldInfo = New-Object Diagnostics.ProcessStartInfo
    $heldInfo.FileName = Join-Path $heldRoot 'VRC-SWITCHEROONIE.exe'
    $heldInfo.WorkingDirectory = $heldRoot
    $heldInfo.UseShellExecute = $false
    $heldInfo.CreateNoWindow = $true
    $heldProcess = [Diagnostics.Process]::Start($heldInfo)
    try {
        $heldRecord = Join-Path $heldRoot 'App/fixture-result.txt'
        $heldDeadline = [DateTime]::UtcNow.AddSeconds(3)
        while (-not (Test-Path -LiteralPath $heldRecord) -and [DateTime]::UtcNow -lt $heldDeadline) { Start-Sleep -Milliseconds 10 }
        Check ((Test-Path -LiteralPath $heldRecord) -and -not $heldProcess.HasExited) 'wrapper retains exact bootstrap lifetime after CreateProcess returns'
        $writeRefused = $false
        try { $writer = [IO.File]::Open((Join-Path $heldRoot 'App/fixture-runtime.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite); $writer.Dispose() }
        catch [IO.IOException] { $writeRefused = $true }
        Check $writeRefused 'verified dependency remains protected by read lease during bootstrap lifetime'
        Remove-Item -LiteralPath $holdMarker
        Check ($heldProcess.WaitForExit(5000) -and $heldProcess.ExitCode -eq 0) 'exact bootstrap exits normally and quiet wrapper then exits without terminating it'
        $writer = [IO.File]::Open((Join-Path $heldRoot 'App/fixture-runtime.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
        Check $writer.CanWrite 'dependency lease releases only after captured bootstrap exit'
        $writer.Dispose()
    } finally {
        if (Test-Path -LiteralPath $holdMarker) { Remove-Item -LiteralPath $holdMarker }
        if (-not $heldProcess.WaitForExit(5000)) { $heldProcess.Kill(); $heldProcess.WaitForExit(); throw 'Owned fixture wrapper exceeded deadline.' }
        $heldProcess.Dispose()
    }
    foreach ($entry in @(@($correct, @(), '--launch'), @($unicode, @('--background'), '--launch|--background'))) {
        $root = [string]$entry[0]
        Check ((Invoke-Wrapper $root $entry[1]) -eq 0) 'verified harmless GUI payload starts via absolute CreateProcessW'
        $record = Join-Path $root 'App/fixture-result.txt'
        $deadline = [DateTime]::UtcNow.AddSeconds(3)
        while (-not (Test-Path -LiteralPath $record) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 20 }
        Check ((Test-Path -LiteralPath $record) -and ([IO.File]::ReadAllText($record) -eq [string]$entry[2])) 'child receives exactly --launch and the optional exact background flag'
    }
} else { $script:skipped += 'CreateProcess routing: optional -LaunchFixture not requested' }
$report = [ordered]@{
    checks=$script:checks; failures=$script:failures; skipped=$script:skipped
    fixturePayloadSha256=$payloadHash
    fixtureWrapperSha256=(Get-FileHash -LiteralPath $wrapper -Algorithm SHA256).Hash
    productionProgramsLaunched=$false; runtimeOrUserStateModified=$false
    harmlessFixtureLaunchRequested=[bool]$LaunchFixture
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $BuildRoot 'validation.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 5
if ($script:failures) { exit 1 }
