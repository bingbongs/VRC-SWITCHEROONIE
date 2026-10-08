param(
    [ValidateSet('Status','Install','Enable','Disable','Uninstall','Relocate')][string]$Action = 'Status',
    [switch]$EnableExperimental
)
$ErrorActionPreference = 'Stop'
function Get-ProcessKnownFolderPath([Guid]$Folder) {
    if (-not ('Switcheroonie.ProcessKnownFolders' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
namespace Switcheroonie {
    public static class ProcessKnownFolders {
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
        public static string Resolve(Guid folder) {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x000E, out token)) throw new IOException("Process profile token unavailable.", new Win32Exception(Marshal.GetLastWin32Error()));
            IntPtr value = IntPtr.Zero;
            try {
                int result = SHGetKnownFolderPath(ref folder, 0, token, out value);
                if (result < 0 || value == IntPtr.Zero) throw new IOException("Process profile resolution failed (0x" + result.ToString("X8") + ").");
                string path = Marshal.PtrToStringUni(value);
                if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new IOException("Process profile directory unavailable.");
                return path;
            } finally {
                if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value);
                CloseHandle(token);
            }
        }
    }
}
'@
    }
    return [Switcheroonie.ProcessKnownFolders]::Resolve($Folder)
}
$profileRoot = Get-ProcessKnownFolderPath ([Guid]'5E6C858F-0E22-4760-9AFE-EA3317B67173')
$localAppDataRoot = Get-ProcessKnownFolderPath ([Guid]'F1B32785-6FBA-4FCF-9D55-7B8E7F157091')
$dataRoot = Join-Path $profileRoot 'VRC-SWITCHEROONIE\config'
# Historical driver ownership only. Never read active configuration/journals from this location.
$legacyDataRoot = Join-Path $localAppDataRoot 'VRC-SWITCHEROONIE'
$legacyInstallRoot = [IO.Path]::GetFullPath((Join-Path $legacyDataRoot 'drivers\0.1.0\switcheroonie'))
$relocatedInstallRoot = [IO.Path]::GetFullPath((Join-Path $profileRoot 'VRC-SWITCHEROONIE\drivers\0.1.0\switcheroonie'))
$allowedOwnedRoots = @($legacyInstallRoot, $relocatedInstallRoot)
# Fresh installs use the location that passed actual SteamVR driver discovery.
# Existing journals still select their exact recorded legacy or relocated root.
$installRoot = $relocatedInstallRoot
$driverConfig = Join-Path $dataRoot 'driver.json'
$journalPath = Join-Path $dataRoot 'installation-journal.json'
$packageDriverRoot = Join-Path ([IO.Directory]::GetParent($PSScriptRoot).FullName) 'driver'
$pathsFile = Join-Path $localAppDataRoot 'openvr\openvrpaths.vrpath'
$paths = Get-Content -LiteralPath $pathsFile -Raw | ConvertFrom-Json
$runtime = [IO.Path]::GetFullPath([string]$paths.runtime[0])
$registryTool = Join-Path $runtime 'bin\win64\vrpathreg.exe'
$manifestPath = Join-Path ([IO.Directory]::GetParent($runtime).Parent.FullName) 'appmanifest_250820.acf'
if (!(Test-Path -LiteralPath $registryTool)) { throw 'SteamVR registration tool is unavailable.' }
$buildMatch = [regex]::Match((Get-Content -LiteralPath $manifestPath -Raw),'"buildid"\s*"(\d+)"')
$buildId = $buildMatch.Groups[1].Value
$active = @(Get-Process vrserver,vrcompositor -ErrorAction SilentlyContinue).Count -gt 0
function Read-ConfigText([string]$Path) {
    # Get-Content strings carry PSPath/PSProvider metadata in Windows PowerShell 5.1.
    # ReadAllText returns plain CLR text so the journal stores a JSON string only.
    return [string]([IO.File]::ReadAllText($Path))
}
function Convert-JournalConfigText($Value, [string]$Field) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return [string]$Value }
    $legacyValue = $Value.PSObject.Properties['value']
    if ($null -ne $legacyValue -and $legacyValue.Value -is [string]) {
        $text = [string]$legacyValue.Value
        try { $parsed = $text | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "Journal $Field legacy value is not valid configuration JSON; refusing recovery." }
        if ($text.TrimStart().StartsWith('{') -and $null -ne $parsed -and $parsed.GetType().FullName -eq 'System.Management.Automation.PSCustomObject') { return [string]$text }
    }
    throw "Journal $Field must be plain text, null, or a legacy value wrapper containing a JSON object; refusing recovery."
}
function Read-Journal {
    $value = (Read-ConfigText $journalPath) | ConvertFrom-Json
    foreach ($field in 'originalConfig','lastConfig') {
        if ($null -eq $value.PSObject.Properties[$field]) { throw "Journal is missing $field; refusing recovery." }
        $value.$field = Convert-JournalConfigText $value.$field $field
    }
    return $value
}
function Save-Journal($value) {
    foreach ($field in 'originalConfig','lastConfig') { $value.$field = Convert-JournalConfigText $value.$field $field }
    $value | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $journalPath -Encoding UTF8
}
function Write-DriverConfig([bool]$Enabled) {
    $config = if (Test-Path -LiteralPath $driverConfig) { (Read-ConfigText $driverConfig) | ConvertFrom-Json } else { [pscustomobject]@{} }
    $config | Add-Member -NotePropertyName experimentalOptIn -NotePropertyValue $Enabled -Force
    $config | Add-Member -NotePropertyName approvedRuntimeBuild -NotePropertyValue '25330290' -Force
    $config | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $driverConfig -Encoding UTF8
    return [string](Read-ConfigText $driverConfig)
}
function Restore-DriverConfig($Journal) {
    if (Test-Path -LiteralPath $driverConfig) {
        $current = [string](Read-ConfigText $driverConfig)
        if ($current -cne $Journal.lastConfig) {
            throw 'driver.json changed after the harness wrote it. Configuration and files retained for manual conflict review.'
        }
        if ($null -ne $Journal.originalConfig) {
            Set-Content -LiteralPath $driverConfig -Value ([string]$Journal.originalConfig) -NoNewline -Encoding UTF8
        } else { Remove-Item -LiteralPath $driverConfig }
    }
}
function Resolve-OwnedInstallRoot([string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root)) { throw 'Owned installation root is missing.' }
    $resolved = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (@($allowedOwnedRoots | Where-Object { $resolved.Equals($_.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase) }).Count -ne 1) {
        throw 'Installation root is outside the exact owned-root allowlist; refusing changes.'
    }
    return [string]$resolved
}
function Get-OwnedInstallRoots($Journal) {
    $roots = @([string]$Journal.installRoot)
    if ($null -ne $Journal.PSObject.Properties['ownedInstallRoots']) { $roots += @($Journal.ownedInstallRoots) }
    return @($roots | ForEach-Object { Resolve-OwnedInstallRoot ([string]$_) } | Select-Object -Unique)
}
function Remove-OwnedInstallRoot([string]$Root) {
    $resolved = Resolve-OwnedInstallRoot $Root
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Owned driver root unexpectedly became a reparse point; refusing recursive cleanup.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
function Get-RegisteredDriverRoots {
    $currentPaths = (Read-ConfigText $pathsFile) | ConvertFrom-Json
    return @($currentPaths.external_drivers)
}
function Set-DriverRegistration([ValidateSet('adddriver','removedriver')][string]$Operation, [string]$Root) {
    $resolved = Resolve-OwnedInstallRoot $Root
    & $registryTool $Operation $resolved
    if ($LASTEXITCODE -ne 0) { throw "Owned driver registration operation $Operation failed; recovery journal retained." }
}
function Assert-ConfigUnchanged($Journal) {
    if (Test-Path -LiteralPath $driverConfig) {
        $current = [string](Read-ConfigText $driverConfig)
        if ($current -cne $Journal.lastConfig) { throw 'driver.json changed after the harness wrote it. Registration, configuration and files retained for manual conflict review.' }
    }
}
function Invoke-OwnedRelocation($Journal) {
    $sourceRoot = Resolve-OwnedInstallRoot ([string]$Journal.installRoot)
    $targetRoot = Resolve-OwnedInstallRoot $relocatedInstallRoot
    if ($sourceRoot -eq $targetRoot) { Write-Host 'Owned driver is already at the proposed location; no changes made.'; return }
    if ($Journal.phase -ne 'installed' -or $Journal.originalRegistration) { throw 'Relocation requires an installed ownership journal without a pre-existing registration to preserve.' }
    if (!(Test-Path -LiteralPath (Join-Path $sourceRoot 'driver.vrdrivermanifest'))) { throw 'Journal-owned source driver is missing.' }
    if (!(Test-Path -LiteralPath (Join-Path $packageDriverRoot 'driver.vrdrivermanifest'))) { throw 'Run Relocate from the built package tools directory so its current owned driver resources are available.' }
    if ((Get-Item -LiteralPath $sourceRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Owned source driver is a reparse point; refusing relocation.' }
    if (Test-Path -LiteralPath $targetRoot) { throw 'Proposed driver directory already exists; refusing to overwrite an unowned staging directory.' }
    Assert-ConfigUnchanged $Journal
    $registeredBefore = @(Get-RegisteredDriverRoots)
    if ($registeredBefore -contains $targetRoot) { throw 'Proposed location has an existing registration; refusing to replace an unknown registration.' }
    $sourceWasRegistered = $registeredBefore -contains $sourceRoot
    $priorJournal = ($Journal | ConvertTo-Json -Depth 6) | ConvertFrom-Json
    $ownedRoots = @((@(Get-OwnedInstallRoots $Journal) + @($targetRoot)) | Select-Object -Unique)
    $Journal | Add-Member -NotePropertyName ownedInstallRoots -NotePropertyValue $ownedRoots -Force
    $Journal | Add-Member -NotePropertyName previousInstallRoot -NotePropertyValue $sourceRoot -Force
    $Journal | Add-Member -NotePropertyName relocationTarget -NotePropertyValue $targetRoot -Force
    $Journal.phase = 'relocating'; Save-Journal $Journal
    try {
        New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
        Get-ChildItem -LiteralPath $sourceRoot -Force | Copy-Item -Destination $targetRoot -Recurse -Force
        Get-ChildItem -LiteralPath $packageDriverRoot -Force | Copy-Item -Destination $targetRoot -Recurse -Force
        $manifest = Join-Path $targetRoot 'driver.vrdrivermanifest'
        $definition = (Read-ConfigText $manifest) | ConvertFrom-Json
        if ($null -ne $definition.PSObject.Properties['directory']) {
            if (![string]::IsNullOrEmpty([string]$definition.directory)) { throw 'Owned manifest has a nonempty directory redirection; refusing to rewrite it.' }
            $definition.PSObject.Properties.Remove('directory')
            $definition | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifest -Encoding UTF8
        }
        Set-DriverRegistration 'adddriver' $targetRoot
        if ($sourceWasRegistered) { Set-DriverRegistration 'removedriver' $sourceRoot }
        $Journal.installRoot = [string]$targetRoot; $Journal.phase = 'installed'; Save-Journal $Journal
        Write-Host 'Relocated the owned registration to the proposed user-profile location. Legacy driver files remain as an owned rollback copy. No runtime was launched or restarted.'
    } catch {
        $failure = $_
        try {
            $currentRegistrations = @(Get-RegisteredDriverRoots)
            if ($sourceWasRegistered -and $currentRegistrations -notcontains $sourceRoot) { Set-DriverRegistration 'adddriver' $sourceRoot }
            if ($currentRegistrations -contains $targetRoot) { Set-DriverRegistration 'removedriver' $targetRoot }
            Remove-OwnedInstallRoot $targetRoot
            Save-Journal $priorJournal
        } catch {
            $Journal.phase = 'relocation-recovery-required'; Save-Journal $Journal
            throw 'Relocation and rollback could not complete. Owned trees and the recovery journal were retained; inspect both exact registered roots before continuing.'
        }
        throw "Relocation failed; prior registration and journal were restored: $($failure.Exception.Message)"
    }
}
$existingJournal = $null
if (Test-Path -LiteralPath $journalPath) {
    $existingJournal = Read-Journal
    $installRoot = Resolve-OwnedInstallRoot ([string]$existingJournal.installRoot)
    $null = Get-OwnedInstallRoots $existingJournal
}
$registered = @(Get-RegisteredDriverRoots) -contains $installRoot
if ($Action -eq 'Status') {
    [pscustomobject]@{installed=Test-Path -LiteralPath $installRoot;driverRoot=$installRoot.Replace($profileRoot,'%USERPROFILE%');registered=$registered;runtimeActive=$active;installedRuntimeBuild=$buildId;supportedBuild='25330290';driverConfigPresent=Test-Path -LiteralPath $driverConfig} | ConvertTo-Json
    return
}
if ($active -and $Action -in @('Install','Enable','Uninstall','Relocate')) { throw 'SteamVR is active. Complete your session and run this command when SteamVR is stopped. This command never stops it.' }
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
if ($Action -eq 'Relocate') {
    if ($null -eq $existingJournal) { throw 'No owned installation journal exists; refusing relocation.' }
    Invoke-OwnedRelocation $existingJournal
    return
}
if ($Action -eq 'Install') {
    if ($EnableExperimental -and $buildId -ne '25330290') { throw "Installed SteamVR build $buildId has not been evaluated by this backend." }
    $source = Join-Path ([IO.Directory]::GetParent($PSScriptRoot).FullName) 'driver'
    if (!(Test-Path -LiteralPath (Join-Path $source 'driver.vrdrivermanifest'))) { throw 'Run Install from the built package tools directory.' }
    if (Test-Path -LiteralPath $journalPath) {
        $priorJournal = Read-Journal
        if ($priorJournal.phase -ne 'uninstalled') { throw 'Installation journal already exists. Use Enable, Disable, or Uninstall; do not overwrite the recovery record.' }
        $archivedJournal = Join-Path $dataRoot ('installation-journal-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff') + '.json')
        Move-Item -LiteralPath $journalPath -Destination $archivedJournal
    }
    $installRoot = $relocatedInstallRoot
    $registered = @(Get-RegisteredDriverRoots) -contains $installRoot
    if (Test-Path -LiteralPath $installRoot) { throw 'An existing driver directory has no active ownership journal. Refusing to overwrite unknown files.' }
    $originalConfig = if (Test-Path -LiteralPath $driverConfig) { [string](Read-ConfigText $driverConfig) } else { $null }
    $journal = [pscustomobject]@{version=1;installRoot=$installRoot;originalRegistration=$registered;originalConfig=$originalConfig;lastConfig=$null;phase='prepared';dateUtc=[DateTime]::UtcNow.ToString('o')}
    Save-Journal $journal
    New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $installRoot -Recurse -Force
    $journal.lastConfig = [string](Write-DriverConfig $EnableExperimental.IsPresent)
    Save-Journal $journal
    if (!$registered) { & $registryTool adddriver $installRoot; if ($LASTEXITCODE -ne 0) { throw 'Driver registration failed; journal retained for recovery.' } }
    $journal.phase = 'installed'; Save-Journal $journal
    Write-Host 'Installed only VRC-SWITCHEROONIE. No runtime was launched or restarted.'
    if (!$EnableExperimental) { Write-Host 'Routing remains disabled. Enable explicitly before a controlled test.' }
    return
}
if ($Action -in @('Enable','Disable')) {
    if ($Action -eq 'Enable' -and (!$registered -or $buildId -ne '25330290')) { throw 'The owned driver must be registered and SteamVR must match build 25330290.' }
    if ($null -ne $existingJournal) { Assert-ConfigUnchanged $existingJournal }
    $written = [string](Write-DriverConfig ($Action -eq 'Enable'))
    if (Test-Path -LiteralPath $journalPath) {
        $journal = Read-Journal
        $journal.lastConfig = [string]$written; Save-Journal $journal
    }
    Write-Host "$Action recorded for next SteamVR startup. Current runtime and applications were not restarted. Use Switcheroonie.Cli.exe release to release current input."
    return
}
if (!(Test-Path -LiteralPath $journalPath)) { throw 'No owned installation journal exists; refusing to remove an unknown installation.' }
$journal = Read-Journal
$ownedRoots = @(Get-OwnedInstallRoots $journal)
Assert-ConfigUnchanged $journal
if (!$journal.originalRegistration) {
    foreach ($ownedRoot in $ownedRoots) {
        if (@(Get-RegisteredDriverRoots) -contains $ownedRoot) { Set-DriverRegistration 'removedriver' $ownedRoot }
    }
}
Restore-DriverConfig $journal
if (!$journal.originalRegistration) { foreach ($ownedRoot in $ownedRoots) { Remove-OwnedInstallRoot $ownedRoot } }
$journal.phase = 'uninstalled'; Save-Journal $journal
Write-Host 'Removed the owned registration and driver; preservation journal retained. Other SteamVR drivers, bindings, trackers, and audio settings were preserved.'
