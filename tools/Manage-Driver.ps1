param(
    [ValidateSet('Status','Install','Enable','Disable','Uninstall','Relocate')][string]$Action = 'Status',
    [switch]$EnableExperimental,
    [switch]$FreshSetupOnly,
    [switch]$OwnedSetupOnly
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
function Test-SetupStoppedInventory([string[]]$Names) {
    $protectedNames = @('VRChat','vrserver','vrcompositor','vrmonitor','vrstartup','switcheroonie-test-scene')
    foreach ($name in $Names) {
        if ($protectedNames -contains $name) { return $false }
    }
    return $true
}
function Get-SetupProcessNames {
    # Enumerate once without suppressing errors. Missing named processes are normal,
    # but an inventory failure never grants permission to change the installation.
    $processes = [Diagnostics.Process]::GetProcesses()
    try {
        $names = New-Object 'System.Collections.Generic.List[string]'
        foreach ($process in $processes) {
            $name = [string]$process.ProcessName
            if ([string]::IsNullOrWhiteSpace($name)) { throw 'Process inventory is unavailable.' }
            $names.Add($name)
        }
        return $names.ToArray()
    } finally {
        foreach ($process in $processes) { $process.Dispose() }
    }
}
function Get-SetupSafetyState {
    try {
        $names = @(Get-SetupProcessNames)
        return [pscustomobject]@{known=$true;stopped=(Test-SetupStoppedInventory $names)}
    } catch {
        return [pscustomobject]@{known=$false;stopped=$false}
    }
}
function Assert-SetupStopped {
    $state = Get-SetupSafetyState
    if (!$state.known) { throw 'Could not verify that VRChat and SteamVR are stopped. Installation changes were deferred; no applications were stopped.' }
    if (!$state.stopped) { throw 'VRChat or SteamVR is active or starting. Complete the session normally before setup. This command never stops an application.' }
}
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
function Get-JournalSerializedText($value) {
    foreach ($field in 'originalConfig','lastConfig') { $value.$field = Convert-JournalConfigText $value.$field $field }
    return [string](($value | ConvertTo-Json -Depth 6) + [Environment]::NewLine)
}
function Save-Journal($value, [switch]$MustBeAbsent, [string]$ExpectedText = $null) {
    $text = Get-JournalSerializedText $value
    if ($MustBeAbsent) {
        $stream = [IO.File]::Open($journalPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes($text)
            $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true)
        } finally { $stream.Dispose() }
    } else {
        if (![string]::IsNullOrEmpty($ExpectedText) -and (!(Test-Path -LiteralPath $journalPath -PathType Leaf) -or (Read-ConfigText $journalPath) -cne $ExpectedText)) {
            throw 'Installation journal changed during fresh setup; the intervening journal was preserved.'
        }
        Set-Content -LiteralPath $journalPath -Value $text -NoNewline -Encoding UTF8
    }
}
function Write-DriverConfig([bool]$Enabled, [switch]$MustBeAbsent, $SetupAuthority = $null) {
    $before = if (Test-Path -LiteralPath $driverConfig) { [string](Read-ConfigText $driverConfig) } else { $null }
    if ($MustBeAbsent -and $null -ne $before) { throw 'driver.json appeared during fresh setup; configuration retained.' }
    $config = if ($null -ne $before) { $before | ConvertFrom-Json } else { [pscustomobject]@{} }
    $config | Add-Member -NotePropertyName experimentalOptIn -NotePropertyValue $Enabled -Force
    $config | Add-Member -NotePropertyName approvedRuntimeBuild -NotePropertyValue '25330290' -Force
    $text = $config | ConvertTo-Json -Depth 4
    $current = if (Test-Path -LiteralPath $driverConfig) { [string](Read-ConfigText $driverConfig) } else { $null }
    if ($current -cne $before) { throw 'driver.json changed while preparing setup; configuration retained.' }
    if ($null -ne $SetupAuthority) { Assert-OwnedSetupAuthority $SetupAuthority }
    Assert-SetupStopped
    # Inventory collection itself can take time. Recheck file authority after it,
    # directly before writing rather than relying on the GUI or earlier reads.
    $latest = if (Test-Path -LiteralPath $driverConfig) { [string](Read-ConfigText $driverConfig) } else { $null }
    if ($latest -cne $before) { throw 'driver.json changed before the setup write; configuration retained.' }
    if ($null -ne $SetupAuthority) {
        Assert-OwnedSetupAuthority $SetupAuthority
        # Resource hashing is bounded but may take time; collect the process
        # inventory again after it, immediately before the flag mutation.
        Assert-SetupStopped
        if ((Read-ConfigText $journalPath) -cne $SetupAuthority.journalText -or (Read-ConfigText $driverConfig) -cne $before) { throw 'Owned setup authority changed before writing; intervening state was preserved.' }
    }
    if ($MustBeAbsent) {
        $stream = [IO.File]::Open($driverConfig,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes($text + [Environment]::NewLine)
            $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true)
        } finally { $stream.Dispose() }
    } else { Set-Content -LiteralPath $driverConfig -Value $text -Encoding UTF8 }
    return [string](Read-ConfigText $driverConfig)
}
function Restore-DriverConfig($Journal) {
    if (Test-Path -LiteralPath $driverConfig) {
        $current = [string](Read-ConfigText $driverConfig)
        if ($current -cne $Journal.lastConfig) {
            throw 'driver.json changed after the harness wrote it. Configuration and files retained for manual conflict review.'
        }
        if ($null -ne $Journal.originalConfig) {
            Assert-SetupStopped
            if ((Read-ConfigText $driverConfig) -cne $Journal.lastConfig) { throw 'driver.json changed before restoration; intervening configuration retained.' }
            Set-Content -LiteralPath $driverConfig -Value ([string]$Journal.originalConfig) -NoNewline -Encoding UTF8
        } else {
            Assert-SetupStopped
            if ((Read-ConfigText $driverConfig) -cne $Journal.lastConfig) { throw 'driver.json changed before restoration; intervening configuration retained.' }
            Remove-Item -LiteralPath $driverConfig
        }
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
    if ($null -ne $Journal.PSObject.Properties['freshSetupRootCreated']) {
        if ($Journal.freshSetupRootCreated -isnot [bool]) { throw 'Fresh setup ownership marker is invalid; refusing cleanup.' }
        if (!$Journal.freshSetupRootCreated) { return @() }
    }
    $roots = @([string]$Journal.installRoot)
    if ($null -ne $Journal.PSObject.Properties['ownedInstallRoots']) { $roots += @($Journal.ownedInstallRoots) }
    return @($roots | ForEach-Object { Resolve-OwnedInstallRoot ([string]$_) } | Select-Object -Unique)
}
function Assert-FreshSetupRegistrations([bool]$CurrentRegistered = $false) {
    $registeredRoots = @(Get-RegisteredDriverRoots)
    foreach ($root in $allowedOwnedRoots) {
        $found = $false
        foreach ($entry in $registeredRoots) {
            if ([string]::IsNullOrWhiteSpace([string]$entry) -or ![IO.Path]::IsPathRooted([string]$entry)) { throw 'Driver registration inventory is invalid; fresh setup was refused.' }
            if ([IO.Path]::GetFullPath([string]$entry).TrimEnd('\').Equals($root.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) { $found = $true }
        }
        $expected = $CurrentRegistered -and $root.Equals($relocatedInstallRoot,[StringComparison]::OrdinalIgnoreCase)
        if ($found -ne $expected) { throw 'An owned-root registration already exists or changed. Fresh setup will not replace an existing installation.' }
    }
}
function Assert-FreshSetupAbsent {
    if ((Test-Path -LiteralPath $journalPath) -or (Test-Path -LiteralPath $driverConfig)) { throw 'Existing driver configuration or installation journal found. Fresh setup will not modify it.' }
    foreach ($root in $allowedOwnedRoots) {
        if (Test-Path -LiteralPath $root) { throw 'An owned driver directory already exists. Fresh setup will not overwrite it.' }
    }
    Assert-FreshSetupRegistrations
}
function Assert-FreshSetupOwnedState([string]$ExpectedJournalText, [switch]$RootCreated, $ExpectedConfigText = $null, [switch]$Registered) {
    if (!(Test-Path -LiteralPath $journalPath -PathType Leaf) -or (Read-ConfigText $journalPath) -cne $ExpectedJournalText) { throw 'Installation journal changed during fresh setup; intervening state was preserved.' }
    $config = if (Test-Path -LiteralPath $driverConfig) { [string](Read-ConfigText $driverConfig) } else { $null }
    if ($config -cne $ExpectedConfigText) { throw 'Driver configuration appeared or changed during fresh setup; intervening state was preserved.' }
    foreach ($root in $allowedOwnedRoots) {
        if ($RootCreated -and $root.Equals($relocatedInstallRoot,[StringComparison]::OrdinalIgnoreCase)) {
            if (!(Test-Path -LiteralPath $root -PathType Container)) { throw 'Fresh owned driver directory changed; setup was refused.' }
            $null = Assert-OwnedDriverPath $root $root
        } elseif (Test-Path -LiteralPath $root) { throw 'An owned driver directory appeared during fresh setup; it was preserved.' }
    }
    Assert-FreshSetupRegistrations $Registered.IsPresent
}
function Assert-OwnedDriverPath([string]$Root, [string]$Path) {
    $resolved = Resolve-OwnedInstallRoot $Root
    $target = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (!$target.Equals($resolved,[StringComparison]::OrdinalIgnoreCase) -and !$target.StartsWith($resolved+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Driver operation escaped the exact owned root; refusing changes.'
    }
    for ($cursor = $target; $null -ne $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Owned driver path contains a reparse point; refusing changes.'
        }
        if ($cursor.Equals($resolved,[StringComparison]::OrdinalIgnoreCase)) { break }
    }
    return [string]$target
}
function New-OwnedDriverDirectory([string]$Root, [string]$Path, [switch]$MustBeAbsent, [string]$FreshJournalText = $null) {
    $target = Assert-OwnedDriverPath $Root $Path
    if ($MustBeAbsent -and -not ('Switcheroonie.SetupDirectories' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
namespace Switcheroonie {
    public static class SetupDirectories {
        [DllImport("kernel32.dll", EntryPoint="CreateDirectoryW", CharSet=CharSet.Unicode, SetLastError=true)]
        static extern bool CreateDirectory(string path, IntPtr security);
        public static void CreateExclusive(string path) {
            if (!CreateDirectory(path, IntPtr.Zero)) throw new IOException("Fresh setup directory could not be created exclusively; existing state was preserved.");
        }
    }
}
'@
    }
    if ($MustBeAbsent) {
        $parent = [IO.Path]::GetDirectoryName($target)
        if (!(Test-Path -LiteralPath $parent)) {
            Assert-SetupStopped
            [IO.Directory]::CreateDirectory($parent) | Out-Null
        }
        if ($null -ne $FreshJournalText) {
            Assert-FreshSetupOwnedState $FreshJournalText -RootCreated:(!$target.Equals($Root,[StringComparison]::OrdinalIgnoreCase))
        }
    }
    Assert-SetupStopped
    if ($MustBeAbsent) { [Switcheroonie.SetupDirectories]::CreateExclusive($target) }
    else { [IO.Directory]::CreateDirectory($target) | Out-Null }
}
function Copy-OwnedDriverTree([string]$Source, [string]$Destination, [switch]$FreshRoot, [string]$FreshJournalText = $null, $FreshOwnershipJournal = $null, $FreshJournalState = $null) {
    if ($FreshRoot -and [string]::IsNullOrEmpty($FreshJournalText)) { throw 'Fresh driver copy requires its exact ownership journal.' }
    if ($FreshRoot -and ($null -eq $FreshOwnershipJournal -or $FreshJournalState -isnot [System.Management.Automation.PSReference])) { throw 'Fresh driver copy requires its ownership marker and state reference.' }
    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd('\')
    $targetRoot = Resolve-OwnedInstallRoot $Destination
    if (!(Test-Path -LiteralPath $sourceRoot -PathType Container) -or ((Get-Item -LiteralPath $sourceRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Driver source is missing or is a reparse point; refusing copy.'
    }
    New-OwnedDriverDirectory $targetRoot $targetRoot -MustBeAbsent:$FreshRoot -FreshJournalText $FreshJournalText
    if ($FreshRoot) {
        # Only a successful exclusive create grants ownership. A racing foreign
        # directory must never become eligible for later uninstall cleanup.
        $FreshOwnershipJournal.freshSetupRootCreated = $true
        Save-Journal $FreshOwnershipJournal -ExpectedText $FreshJournalText
        $FreshJournalText = Get-JournalSerializedText $FreshOwnershipJournal
        $FreshJournalState.Value = $FreshJournalText
    }
    $directories = New-Object 'System.Collections.Generic.Stack[string]'
    $directories.Push($sourceRoot)
    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Driver source contains a reparse point; refusing copy.' }
            $relative = $item.FullName.Substring($sourceRoot.Length+1)
            $target = Assert-OwnedDriverPath $targetRoot (Join-Path $targetRoot $relative)
            if ($FreshRoot) { Assert-FreshSetupOwnedState $FreshJournalText -RootCreated }
            if ($item.PSIsContainer) {
                New-OwnedDriverDirectory $targetRoot $target -MustBeAbsent:$FreshRoot -FreshJournalText $FreshJournalText
                $directories.Push($item.FullName)
            } else {
                Assert-SetupStopped
                if ($FreshRoot) { [IO.File]::Copy($item.FullName,$target,$false) }
                else { Copy-Item -LiteralPath $item.FullName -Destination $target -Force }
            }
        }
    }
}
function Remove-OwnedInstallRoot([string]$Root) {
    $resolved = Resolve-OwnedInstallRoot $Root
    if (Test-Path -LiteralPath $resolved) {
        $null = Assert-OwnedDriverPath $resolved $resolved
        $directories = New-Object 'System.Collections.Generic.Stack[string]'
        $ordered = New-Object 'System.Collections.Generic.List[string]'
        $directories.Push($resolved)
        while ($directories.Count -gt 0) {
            $directory = $directories.Pop()
            $ordered.Add($directory)
            foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force)) {
                $target = Assert-OwnedDriverPath $resolved $item.FullName
                if ($item.PSIsContainer) { $directories.Push($target) }
                else {
                    Assert-SetupStopped
                    [IO.File]::Delete($target)
                }
            }
        }
        for ($index = $ordered.Count-1; $index -ge 0; --$index) {
            $target = Assert-OwnedDriverPath $resolved $ordered[$index]
            Assert-SetupStopped
            # Never recurse: each deletion has its own fresh stopped check.
            [IO.Directory]::Delete($target,$false)
        }
    }
}
function Get-RegisteredDriverRoots {
    $currentPaths = (Read-ConfigText $pathsFile) | ConvertFrom-Json
    return @($currentPaths.external_drivers)
}
function Get-DriverResourceInventory([string]$Root) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (!(Test-Path -LiteralPath $rootPath -PathType Container) -or ((Get-Item -LiteralPath $rootPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Driver resource directory is unavailable or redirected.' }
    $items = New-Object 'System.Collections.Generic.List[object]'
    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $directories = New-Object 'System.Collections.Generic.Stack[string]'; $directories.Push($rootPath)
    $directoryCount = 1
    $bytes = [long]0
    while ($directories.Count -gt 0) {
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directories.Pop())) {
            $item = Get-Item -LiteralPath $entry -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Driver resources contain a reparse point; setup was refused.' }
            if ($item.PSIsContainer) {
                ++$directoryCount
                if ($directoryCount -gt 256) { throw 'Driver resource directory inventory exceeds its bound.' }
                $directories.Push($item.FullName); continue
            }
            $relative = $item.FullName.Substring($rootPath.Length+1).Replace('\','/')
            $bytes += $item.Length
            if ($items.Count -ge 256 -or $bytes -gt 64MB -or !$names.Add($relative)) { throw 'Driver resource inventory exceeds its bounds or contains aliases.' }
            $items.Add([pscustomobject]@{path=$relative;bytes=$item.Length;sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash})
        }
    }
    foreach ($required in @('driver.vrdrivermanifest','bin/win64/driver_switcheroonie.dll')) {
        if (!$names.Contains($required)) { throw 'Required owned driver resources are missing.' }
    }
    return @($items.ToArray() | Sort-Object path)
}
function Get-OwnedDriverResourceMatch([string]$Root) {
    $null = Assert-OwnedDriverPath $Root $Root
    $installed = @(Get-DriverResourceInventory $Root)
    $packaged = @(Get-DriverResourceInventory $packageDriverRoot)
    if ($installed.Count -ne $packaged.Count) { throw 'Installed driver files do not match this package. Use the signed updater rather than Enable.' }
    for ($index=0; $index -lt $installed.Count; ++$index) {
        if ($installed[$index].path -ine $packaged[$index].path -or $installed[$index].bytes -ne $packaged[$index].bytes -or $installed[$index].sha256 -ine $packaged[$index].sha256) {
            throw 'Installed driver resources do not match this package. Use the signed updater rather than Enable.'
        }
    }
    return [string](($packaged | ConvertTo-Json -Depth 4 -Compress))
}
function Assert-OwnedSetupRegistration([string]$Root) {
    $registered = @(Get-RegisteredDriverRoots | ForEach-Object {
        if ([string]::IsNullOrWhiteSpace([string]$_) -or ![IO.Path]::IsPathRooted([string]$_)) { throw 'Driver registration inventory is invalid.' }
        [IO.Path]::GetFullPath([string]$_).TrimEnd('\')
    })
    if ($registered -notcontains $Root) { throw 'The exact owned driver is no longer registered.' }
    foreach ($other in $allowedOwnedRoots) {
        if (!$other.Equals($Root,[StringComparison]::OrdinalIgnoreCase) -and $registered -contains $other) { throw 'Another historical owned root is registered; GUI Enable was refused.' }
    }
}
function Get-OwnedSetupAuthority {
    if (!(Test-Path -LiteralPath $journalPath -PathType Leaf) -or !(Test-Path -LiteralPath $driverConfig -PathType Leaf)) { throw 'Owned GUI Enable requires its existing installed journal and configuration.' }
    $journalText = [string](Read-ConfigText $journalPath)
    $journal = $journalText | ConvertFrom-Json
    foreach ($field in @('originalConfig','lastConfig')) {
        if ($null -eq $journal.PSObject.Properties[$field]) { throw 'Owned GUI Enable journal is incomplete.' }
        $journal.$field = Convert-JournalConfigText $journal.$field $field
    }
    if ($journal.version -isnot [int] -or $journal.version -ne 1 -or $journal.phase -cne 'installed' -or $journal.originalRegistration -isnot [bool] -or $journal.originalRegistration) { throw 'Owned GUI Enable requires a complete version-1 owned installation journal.' }
    if ($null -ne $journal.PSObject.Properties['freshSetupRootCreated'] -and ($journal.freshSetupRootCreated -isnot [bool] -or !$journal.freshSetupRootCreated)) { throw 'Fresh setup never acquired this driver directory; GUI Enable was refused.' }
    $root = Resolve-OwnedInstallRoot ([string]$journal.installRoot)
    $null = Get-OwnedInstallRoots $journal
    $configText = [string](Read-ConfigText $driverConfig)
    if ($null -eq $journal.lastConfig -or $configText -cne $journal.lastConfig) { throw 'Owned driver configuration changed; GUI Enable was refused.' }
    $config = $configText | ConvertFrom-Json
    if ($config.experimentalOptIn -isnot [bool] -or $config.experimentalOptIn) { throw 'Owned GUI Enable requires an unchanged disabled configuration.' }
    Assert-OwnedSetupRegistration $root
    $resources = Get-OwnedDriverResourceMatch $root
    return [pscustomobject]@{journalText=$journalText;configText=$configText;root=$root;resources=$resources}
}
function Assert-OwnedSetupAuthority($Authority) {
    $current = Get-OwnedSetupAuthority
    if ($current.journalText -cne $Authority.journalText -or $current.configText -cne $Authority.configText -or $current.root -ine $Authority.root -or $current.resources -cne $Authority.resources) { throw 'Owned setup state changed during GUI Enable; intervening state was preserved.' }
}
function Assert-OwnedSetupAfterWrite($Authority, [string]$Written) {
    if (!(Test-Path -LiteralPath $journalPath -PathType Leaf) -or (Read-ConfigText $journalPath) -cne $Authority.journalText -or !(Test-Path -LiteralPath $driverConfig -PathType Leaf) -or (Read-ConfigText $driverConfig) -cne $Written) { throw 'Owned setup state changed before journal save; intervening state was preserved.' }
    Assert-OwnedSetupRegistration $Authority.root
    if ((Get-OwnedDriverResourceMatch $Authority.root) -cne $Authority.resources) { throw 'Owned driver resources changed before journal save; recovery state was retained.' }
}
function Set-DriverRegistration([ValidateSet('adddriver','removedriver')][string]$Operation, [string]$Root) {
    $resolved = Resolve-OwnedInstallRoot $Root
    Assert-SetupStopped
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
    Assert-SetupStopped
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
    Assert-SetupStopped
    $Journal.phase = 'relocating'; Save-Journal $Journal
    try {
        Copy-OwnedDriverTree $sourceRoot $targetRoot
        Copy-OwnedDriverTree $packageDriverRoot $targetRoot
        $manifest = Join-Path $targetRoot 'driver.vrdrivermanifest'
        $definition = (Read-ConfigText $manifest) | ConvertFrom-Json
        if ($null -ne $definition.PSObject.Properties['directory']) {
            if (![string]::IsNullOrEmpty([string]$definition.directory)) { throw 'Owned manifest has a nonempty directory redirection; refusing to rewrite it.' }
            $definition.PSObject.Properties.Remove('directory')
            $text = $definition | ConvertTo-Json -Depth 6
            $null = Assert-OwnedDriverPath $targetRoot $manifest
            Assert-SetupStopped
            Set-Content -LiteralPath $manifest -Value $text -Encoding UTF8
        }
        Set-DriverRegistration 'adddriver' $targetRoot
        if ($sourceWasRegistered) { Set-DriverRegistration 'removedriver' $sourceRoot }
        Assert-SetupStopped
        $Journal.installRoot = [string]$targetRoot; $Journal.phase = 'installed'; Save-Journal $Journal
        Write-Host 'Relocated the owned registration to the proposed user-profile location. Legacy driver files remain as an owned rollback copy. No runtime was launched or restarted.'
    } catch {
        $failure = $_
        try {
            Assert-SetupStopped
            $currentRegistrations = @(Get-RegisteredDriverRoots)
            if ($sourceWasRegistered -and $currentRegistrations -notcontains $sourceRoot) { Set-DriverRegistration 'adddriver' $sourceRoot }
            if ($currentRegistrations -contains $targetRoot) { Set-DriverRegistration 'removedriver' $targetRoot }
            Remove-OwnedInstallRoot $targetRoot
            Assert-SetupStopped
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
    $safety = Get-SetupSafetyState
    [pscustomobject]@{installed=Test-Path -LiteralPath $installRoot;driverRoot=$installRoot.Replace($profileRoot,'%USERPROFILE%');registered=$registered;runtimeActive=(!$safety.stopped);runtimeInventoryKnown=$safety.known;installedRuntimeBuild=$buildId;supportedBuild='25330290';driverConfigPresent=Test-Path -LiteralPath $driverConfig} | ConvertTo-Json
    return
}
Assert-SetupStopped
if ($FreshSetupOnly -and $OwnedSetupOnly) { throw 'FreshSetupOnly and OwnedSetupOnly cannot be combined.' }
if ($OwnedSetupOnly) {
    if ($Action -ne 'Enable') { throw 'OwnedSetupOnly applies only to Enable.' }
    $ownedSetupAuthority = Get-OwnedSetupAuthority
}
if ($FreshSetupOnly) {
    if ($Action -ne 'Install') { throw 'FreshSetupOnly applies only to Install.' }
    Assert-FreshSetupAbsent
    Assert-SetupStopped
}
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
        if ($FreshSetupOnly) { throw 'An installation journal appeared; fresh setup will not archive or replace it.' }
        $priorJournal = Read-Journal
        if ($priorJournal.phase -ne 'uninstalled') { throw 'Installation journal already exists. Use Enable, Disable, or Uninstall; do not overwrite the recovery record.' }
        $archivedJournal = Join-Path $dataRoot ('installation-journal-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff') + '.json')
        Assert-SetupStopped
        Move-Item -LiteralPath $journalPath -Destination $archivedJournal
    }
    $installRoot = $relocatedInstallRoot
    $registered = @(Get-RegisteredDriverRoots) -contains $installRoot
    if (Test-Path -LiteralPath $installRoot) { throw 'An existing driver directory has no active ownership journal. Refusing to overwrite unknown files.' }
    $originalConfig = if (Test-Path -LiteralPath $driverConfig) { [string](Read-ConfigText $driverConfig) } else { $null }
    $journal = [pscustomobject]@{version=1;installRoot=$installRoot;originalRegistration=$registered;originalConfig=$originalConfig;lastConfig=$originalConfig;phase='prepared';dateUtc=[DateTime]::UtcNow.ToString('o')}
    if ($FreshSetupOnly) { $journal | Add-Member -NotePropertyName freshSetupRootCreated -NotePropertyValue $false }
    if ($FreshSetupOnly) { Assert-FreshSetupAbsent }
    Assert-SetupStopped
    Save-Journal $journal -MustBeAbsent:$FreshSetupOnly
    $freshJournalText = if ($FreshSetupOnly) { Get-JournalSerializedText $journal } else { $null }
    try {
        Copy-OwnedDriverTree $source $installRoot -FreshRoot:$FreshSetupOnly -FreshJournalText $freshJournalText -FreshOwnershipJournal $journal -FreshJournalState ([ref]$freshJournalText)
        if ($FreshSetupOnly) { Assert-FreshSetupOwnedState $freshJournalText -RootCreated }
        $journal.lastConfig = [string](Write-DriverConfig $EnableExperimental.IsPresent -MustBeAbsent:$FreshSetupOnly)
        Save-Journal $journal -ExpectedText $freshJournalText
        if ($FreshSetupOnly) { $freshJournalText = Get-JournalSerializedText $journal }
        if (!$registered) {
            if ($FreshSetupOnly) { Assert-FreshSetupOwnedState $freshJournalText -RootCreated -ExpectedConfigText $journal.lastConfig }
            Set-DriverRegistration 'adddriver' $installRoot
        }
        if ($FreshSetupOnly) { Assert-FreshSetupOwnedState $freshJournalText -RootCreated -ExpectedConfigText $journal.lastConfig -Registered }
        Assert-SetupStopped
        $journal.phase = 'installed'; Save-Journal $journal -ExpectedText $freshJournalText
    } catch {
        $journal.phase = 'install-recovery-required'; Save-Journal $journal -ExpectedText $freshJournalText
        throw 'Installation was interrupted. Owned files and the configuration recovery journal were retained; retry recovery after normal shutdown.'
    }
    Write-Host 'Installed only VRC-SWITCHEROONIE. No runtime was launched or restarted.'
    if (!$EnableExperimental) { Write-Host 'Routing remains disabled. Enable explicitly before a controlled test.' }
    return
}
if ($Action -in @('Enable','Disable')) {
    if ($Action -eq 'Enable' -and (!$registered -or $buildId -ne '25330290')) { throw 'The owned driver must be registered and SteamVR must match build 25330290.' }
    if ($null -ne $existingJournal) { Assert-ConfigUnchanged $existingJournal }
    $authority = if ($OwnedSetupOnly) { $ownedSetupAuthority } else { $null }
    $written = [string](Write-DriverConfig ($Action -eq 'Enable') -SetupAuthority $authority)
    if (Test-Path -LiteralPath $journalPath) {
        if ($OwnedSetupOnly) { Assert-OwnedSetupAfterWrite $authority $written }
        $journal = Read-Journal
        $journal.lastConfig = [string]$written
        if ($OwnedSetupOnly) { Save-Journal $journal -ExpectedText $authority.journalText }
        else { Save-Journal $journal }
    }
    Write-Host "$Action recorded for next SteamVR startup. Current runtime and applications were not restarted. Use Switcheroonie.Cli.exe release to release current input."
    return
}
if (!(Test-Path -LiteralPath $journalPath)) { throw 'No owned installation journal exists; refusing to remove an unknown installation.' }
$journal = Read-Journal
$ownedRoots = @(Get-OwnedInstallRoots $journal)
Assert-ConfigUnchanged $journal
Assert-SetupStopped
$journal.phase = 'uninstalling'; Save-Journal $journal
try {
    if (!$journal.originalRegistration) {
        foreach ($ownedRoot in $ownedRoots) {
            if (@(Get-RegisteredDriverRoots) -contains $ownedRoot) { Set-DriverRegistration 'removedriver' $ownedRoot }
        }
    }
    Restore-DriverConfig $journal
    # Record a completed configuration restoration even if file cleanup must wait.
    $journal.lastConfig = if (Test-Path -LiteralPath $driverConfig) { [string](Read-ConfigText $driverConfig) } else { $null }
    Save-Journal $journal
    if (!$journal.originalRegistration) { foreach ($ownedRoot in $ownedRoots) { Remove-OwnedInstallRoot $ownedRoot } }
    Assert-SetupStopped
    $journal.phase = 'uninstalled'; Save-Journal $journal
} catch {
    $journal.phase = 'uninstall-recovery-required'; Save-Journal $journal
    throw 'Uninstall was interrupted. Remaining owned files, registration and the configuration recovery journal were retained; retry after normal shutdown.'
}
Write-Host 'Removed the owned registration and driver; preservation journal retained. Other SteamVR drivers, bindings, trackers, and audio settings were preserved.'
