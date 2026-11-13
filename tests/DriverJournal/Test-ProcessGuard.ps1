param([string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')))
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Run this regression test with Windows PowerShell 5.1.' }
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepositoryRoot 'tools/Manage-Driver.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Installer does not parse in Windows PowerShell 5.1.'}
# No top-level known-folder/runtime discovery is executed. All paths and inventory
# providers below belong only to this fixture, including the registration command.
foreach($name in @('Test-SetupStoppedInventory','Get-SetupSafetyState','Assert-SetupStopped','Read-ConfigText','Convert-JournalConfigText','Read-Journal','Get-JournalSerializedText','Save-Journal','Write-DriverConfig','Restore-DriverConfig','Resolve-OwnedInstallRoot','Get-OwnedInstallRoots','Assert-FreshSetupRegistrations','Assert-FreshSetupAbsent','Assert-FreshSetupOwnedState','Assert-OwnedDriverPath','New-OwnedDriverDirectory','Copy-OwnedDriverTree','Remove-OwnedInstallRoot','Set-DriverRegistration','Get-RegisteredDriverRoots','Get-DriverResourceInventory','Get-OwnedDriverResourceMatch','Assert-OwnedSetupRegistration','Get-OwnedSetupAuthority','Assert-OwnedSetupAuthority','Assert-OwnedSetupAfterWrite','Assert-ConfigUnchanged','Invoke-OwnedRelocation')) {
    $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    if($null -eq $definition){throw "Missing owned function $name"}
    Invoke-Expression $definition.Extent.Text
}
$statements=@($ast.EndBlock.Statements);$start=-1
for($index=0;$index -lt $statements.Count;++$index){
    $statement=$statements[$index]
    if($statement -is [Management.Automation.Language.AssignmentStatementAst] -and $statement.Left -is [Management.Automation.Language.VariableExpressionAst] -and $statement.Left.VariablePath.UserPath -eq 'existingJournal'){$start=$index;break}
}
if($start -lt 0){throw 'Missing installer action dispatch.'}
$actionTail=(@($statements[$start..($statements.Count-1)] | ForEach-Object {$_.Extent.Text}) -join "`r`n")
# Redirect only package discovery to the fixture package; action/guard code is exact.
$sourceAssignment='$source = Join-Path ([IO.Directory]::GetParent($PSScriptRoot).FullName) ''driver'''
if(!$actionTail.Contains($sourceAssignment)){throw 'Missing installer package source assignment.'}
$actionTail=$actionTail.Replace($sourceAssignment,'$source = $packageDriverRoot')
$sandboxBase=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'VRC-SWITCHEROONIE-DriverJournal-ProcessGuard'))
function Assert-FixtureOrdinaryPath([string]$Path) {
    for($cursor=[IO.Path]::GetFullPath($Path);$null -ne $cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if(!(Test-Path -LiteralPath $cursor)){continue}
        $item=Get-Item -LiteralPath $cursor -Force
        if(!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Fixture sandbox requires ordinary directory ancestors.'}
    }
}
# Registration snapshots are deliberately rewritten often. Keep this disposable
# fixture outside cloud synchronization without retrying any operation or changing
# a product assertion. Unknown or redirected temporary roots fail closed.
Assert-FixtureOrdinaryPath $sandboxBase
[IO.Directory]::CreateDirectory($sandboxBase)|Out-Null
Assert-FixtureOrdinaryPath $sandboxBase
$sandbox=Join-Path $sandboxBase ([Guid]::NewGuid().ToString('N'))
if(-not ('Switcheroonie.DriverJournalFixtureDirectory' -as [type])){
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace Switcheroonie { public static class DriverJournalFixtureDirectory {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateDirectoryW(string path, IntPtr security);
} }
'@
}
Assert-FixtureOrdinaryPath $sandboxBase
if(![Switcheroonie.DriverJournalFixtureDirectory]::CreateDirectoryW($sandbox,[IntPtr]::Zero)){throw 'Exclusive fixture sandbox creation failed.'}
Assert-FixtureOrdinaryPath $sandbox
$profileRoot=$sandbox;$localAppDataRoot=$sandbox
$dataRoot=Join-Path $sandbox 'config';$driverConfig=Join-Path $dataRoot 'driver.json';$journalPath=Join-Path $dataRoot 'installation-journal.json'
$legacyInstallRoot=Join-Path $sandbox 'legacy';$relocatedInstallRoot=Join-Path $sandbox 'current';$allowedOwnedRoots=@($legacyInstallRoot,$relocatedInstallRoot)
$packageDriverRoot=Join-Path $sandbox 'package';$pathsFile=Join-Path $sandbox 'openvrpaths.json'
$registryTool='Invoke-FixtureRegistration';$buildId='25330290';$EnableExperimental=[switch]$true
$results=New-Object 'System.Collections.Generic.List[object]';$exitCode=0
$script:fakeNames=@();$script:inventoryFailure=$false;$script:probeCount=0;$script:blockAt=0
$script:fakeRegistrations=@();$script:trace=@();$script:startAfterTargetRegistration=$false;$script:stopWhenInstallCopied=$false;$script:stopAfterRestoration=$false
$script:injectFreshState='';$script:injectedText=$null
$script:ownedRace=''
$script:originalText='{"custom":"Original-A","experimentalOptIn":false}'
function Assert([bool]$Condition,[string]$Name){$results.Add([pscustomobject]@{name=$Name;passed=$Condition});if(!$Condition){throw "FAILED: $Name"};Write-Output "PASS $Name"}
function Get-SetupProcessNames {
    $script:probeCount++
    if($script:inventoryFailure){throw 'Injected unknown inventory.'}
    if($script:blockAt -gt 0 -and $script:probeCount -ge $script:blockAt){return @('vrstartup')}
    if($script:stopWhenInstallCopied -and (Test-Path -LiteralPath (Join-Path $relocatedInstallRoot 'second.txt'))){return @('vrmonitor')}
    if($script:stopAfterRestoration -and (Test-Path -LiteralPath $driverConfig) -and (Read-ConfigText $driverConfig) -ceq $script:originalText){return @('VRChat')}
    if($script:ownedRace -and $script:probeCount -eq 2){
        switch($script:ownedRace){
            'config' {[IO.File]::WriteAllText($driverConfig,'{"foreign":"owned-config-preserved","experimentalOptIn":false}')}
            'journal' {$journal=Read-Journal;$journal.phase='prepared';Save-Journal $journal}
            'resource' {[IO.File]::WriteAllText((Join-Path $relocatedInstallRoot 'bin/win64/driver_switcheroonie.dll'),'foreign-driver-preserved')}
            'registration' {$script:fakeRegistrations+=@($legacyInstallRoot);Sync-FixtureRegistrations}
        }
        $script:ownedRace=''
    }
    if($script:injectFreshState -eq 'root' -and (Test-Path -LiteralPath $journalPath) -and !(Test-Path -LiteralPath $relocatedInstallRoot)){
        [IO.Directory]::CreateDirectory($relocatedInstallRoot)|Out-Null
        [IO.File]::WriteAllText((Join-Path $relocatedInstallRoot 'foreign.txt'),'foreign-root-preserved')
        $script:injectFreshState=''
    } elseif($script:injectFreshState -and (Test-Path -LiteralPath $relocatedInstallRoot) -and @(Get-ChildItem -LiteralPath $relocatedInstallRoot -File).Count -gt 0){
        switch($script:injectFreshState){
            'config' {$script:injectedText='{"foreign":"config-preserved"}';[IO.File]::WriteAllText($driverConfig,$script:injectedText)}
            'journal' {$script:injectedText='{"foreign":"journal-preserved"}';[IO.File]::WriteAllText($journalPath,$script:injectedText)}
            'file' {$script:injectedText='foreign-file-preserved';[IO.File]::WriteAllText((Join-Path $relocatedInstallRoot 'second.txt'),$script:injectedText)}
        }
        $script:injectFreshState=''
    }
    return @($script:fakeNames)
}
function Sync-FixtureRegistrations {
    [pscustomobject]@{external_drivers=@($script:fakeRegistrations)} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $pathsFile -Encoding UTF8
}
function Invoke-FixtureRegistration([string]$Operation,[string]$Root){
    $script:trace+=@("$Operation|$Root")
    if($Operation -eq 'adddriver'){$script:fakeRegistrations=@($script:fakeRegistrations+$Root|Select-Object -Unique)}
    else{$script:fakeRegistrations=@($script:fakeRegistrations|Where-Object {$_ -ne $Root})}
    Sync-FixtureRegistrations
    if($script:startAfterTargetRegistration -and $Operation -eq 'adddriver' -and $Root -eq $relocatedInstallRoot){$script:fakeNames=@('vrstartup')}
    $global:LASTEXITCODE=0
}
function Reset-Fixture {
    $script:fakeNames=@();$script:inventoryFailure=$false;$script:probeCount=0;$script:blockAt=0
    $script:startAfterTargetRegistration=$false;$script:stopWhenInstallCopied=$false;$script:stopAfterRestoration=$false
    $script:injectFreshState='';$script:injectedText=$null
    $script:ownedRace=''
    # Fixture reset uses only these verified test-owned roots, never installer dispatch.
    foreach($path in @($dataRoot,$legacyInstallRoot,$relocatedInstallRoot,$packageDriverRoot)){
        $resolved=[IO.Path]::GetFullPath($path)
        if(!$resolved.StartsWith($sandbox+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture reset path.'}
        Assert-FixtureOrdinaryPath $resolved
        if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
    }
    New-Item -ItemType Directory -Path $dataRoot,$legacyInstallRoot,$packageDriverRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $legacyInstallRoot 'driver.vrdrivermanifest'),'{}')
    [IO.File]::WriteAllText((Join-Path $legacyInstallRoot 'first.txt'),'old-owned-content')
    [IO.File]::WriteAllText((Join-Path $packageDriverRoot 'driver.vrdrivermanifest'),'{"name":"switcheroonie","directory":"","priority":20000}')
    [IO.File]::WriteAllText((Join-Path $packageDriverRoot 'first.txt'),'new-owned-content')
    [IO.File]::WriteAllText((Join-Path $packageDriverRoot 'second.txt'),'second-owned-content')
    [IO.File]::WriteAllText($driverConfig,'{"custom":"Owned-A","experimentalOptIn":true}')
    $script:fakeRegistrations=@($legacyInstallRoot);$script:trace=@();Sync-FixtureRegistrations
    $journal=[pscustomobject]@{version=1;installRoot=$legacyInstallRoot;originalRegistration=$false;originalConfig=$script:originalText;lastConfig=[string](Read-ConfigText $driverConfig);phase='installed'}
    Save-Journal $journal
    return $journal
}
function Invoke-FixtureAction([string]$Action, [switch]$FreshSetupOnly, [switch]$OwnedSetupOnly){
    $installRoot=$relocatedInstallRoot
    Invoke-Expression $actionTail
}
function Reset-FreshFixture {
    $null=Reset-Fixture
    foreach($path in @($journalPath,$driverConfig,$legacyInstallRoot)){
        $resolved=[IO.Path]::GetFullPath($path)
        if(!$resolved.StartsWith($sandbox+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fresh fixture reset path.'}
        if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
    }
    $script:fakeRegistrations=@();Sync-FixtureRegistrations
}
function Get-FixtureFingerprint {
    $items=@(Get-ChildItem -LiteralPath $sandbox -Recurse -Force|Sort-Object FullName|ForEach-Object{
        $relative=$_.FullName.Substring($sandbox.Length+1)
        if($_.PSIsContainer){'D:'+ $relative}else{'F:'+ $relative+':'+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    return [string]($items -join '|')
}
function Reset-OwnedFixture {
    Reset-FreshFixture
    [IO.Directory]::CreateDirectory((Join-Path $packageDriverRoot 'bin/win64'))|Out-Null
    [IO.File]::WriteAllText((Join-Path $packageDriverRoot 'bin/win64/driver_switcheroonie.dll'),'fake-owned-driver-never-loaded')
    Invoke-FixtureAction 'Install' -FreshSetupOnly
    Invoke-FixtureAction 'Disable'
    $journal=Read-Journal
    $config=Read-ConfigText $driverConfig|ConvertFrom-Json
    $config|Add-Member -NotePropertyName custom -NotePropertyValue 'Keep-me'
    [IO.File]::WriteAllText($driverConfig,($config|ConvertTo-Json))
    $journal.lastConfig=Read-ConfigText $driverConfig;Save-Journal $journal
    $script:probeCount=0;$script:trace=@()
}
try {
    Assert (Test-SetupStoppedInventory @()) 'Empty injected inventory allows setup'
    Assert (Test-SetupStoppedInventory @('Notepad','Switcheroonie.UI','Switcheroonie.Broker')) 'Unrelated processes and resident owned service do not self-block setup'
    foreach($name in @('VRChat','vrserver','vrcompositor','vrmonitor','vrstartup','switcheroonie-test-scene')){
        Assert (!(Test-SetupStoppedInventory @($name.ToUpperInvariant()))) "$name is protected case-insensitively"
    }
    foreach($action in @('Install','Enable','Disable','Uninstall','Relocate')){
        $journal=Reset-Fixture;$before=Read-ConfigText $driverConfig;$journalBefore=Read-ConfigText $journalPath;$script:fakeNames=@('vrstartup')
        $refused=$false;try{Invoke-FixtureAction $action}catch{$refused=$true}
        Assert ($refused -and (Read-ConfigText $driverConfig) -ceq $before -and (Read-ConfigText $journalPath) -ceq $journalBefore -and $script:trace.Count -eq 0 -and !(Test-Path -LiteralPath $relocatedInstallRoot)) "$action refuses an active startup stub before mutation"
    }
    $journal=Reset-Fixture;$script:fakeNames=@('VRChat');$before=Read-ConfigText $journalPath
    $status=(Invoke-FixtureAction 'Status')|ConvertFrom-Json
    Assert ($status.runtimeActive -and $status.runtimeInventoryKnown -and (Read-ConfigText $journalPath) -ceq $before -and $script:trace.Count -eq 0) 'Status remains available and read-only while protected processes are active'
    $script:inventoryFailure=$true;$status=(Invoke-FixtureAction 'Status')|ConvertFrom-Json
    Assert ($status.runtimeActive -and !$status.runtimeInventoryKnown) 'Status reports unknown inventory conservatively without mutation'
    $before=Read-ConfigText $driverConfig;$refused=$false;try{Invoke-FixtureAction 'Disable'}catch{$refused=$true}
    Assert ($refused -and (Read-ConfigText $driverConfig) -ceq $before -and $script:trace.Count -eq 0) 'Inventory failure refuses a mutating action rather than granting permission'

    $journal=Reset-Fixture;$script:fakeNames=@('vrcompositor');$before=Read-ConfigText $driverConfig
    $refused=$false;try{$null=Write-DriverConfig $false}catch{$refused=$true}
    Assert ($refused -and (Read-ConfigText $driverConfig) -ceq $before) 'Config write leaf rechecks the fresh inventory'
    $refused=$false;try{Restore-DriverConfig $journal}catch{$refused=$true}
    Assert ($refused -and (Read-ConfigText $driverConfig) -ceq $before) 'Config restoration write leaf rechecks the fresh inventory'
    $journal.originalConfig=$null;$refused=$false;try{Restore-DriverConfig $journal}catch{$refused=$true}
    Assert ($refused -and (Test-Path -LiteralPath $driverConfig)) 'Config restoration delete leaf rechecks the fresh inventory'
    $refused=$false;try{Set-DriverRegistration 'removedriver' $legacyInstallRoot}catch{$refused=$true}
    Assert ($refused -and $script:trace.Count -eq 0 -and $script:fakeRegistrations -contains $legacyInstallRoot) 'Registration leaf refuses before the injected command executes'

    foreach($remove in @($false,$true)) {
        $journal=Reset-Fixture;if($remove){$journal.originalConfig=$null}
        $script:probeCount=1;$script:ownedRace='config';$refused=$false
        try{Restore-DriverConfig $journal}catch{$refused=$true}
        Assert ($refused -and (Read-ConfigText $driverConfig) -ceq '{"foreign":"owned-config-preserved","experimentalOptIn":false}') ('Config restoration preserves a conflict arising during inventory (delete={0})' -f $remove)
    }

    $journal=Reset-Fixture;$script:blockAt=3;$refused=$false
    try{Copy-OwnedDriverTree $legacyInstallRoot $relocatedInstallRoot}catch{$refused=$true}
    Assert ($refused -and @(Get-ChildItem -LiteralPath $relocatedInstallRoot -File).Count -eq 1 -and $script:probeCount -eq 3) 'Driver copy stops before the next file when a runtime appears'
    $script:blockAt=0;$script:probeCount=0;Copy-OwnedDriverTree $legacyInstallRoot $relocatedInstallRoot
    $script:blockAt=2;$script:probeCount=0;$refused=$false
    try{Remove-OwnedInstallRoot $relocatedInstallRoot}catch{$refused=$true}
    Assert ($refused -and (Test-Path -LiteralPath $relocatedInstallRoot) -and @(Get-ChildItem -LiteralPath $relocatedInstallRoot -File).Count -eq 1) 'Driver deletion stops between files and retains the incomplete owned tree'

    $journal=Reset-Fixture;$script:blockAt=5;$refused=$false
    try{Invoke-OwnedRelocation $journal}catch{$refused=$true}
    $after=Read-Journal
    Assert ($refused -and $after.phase -eq 'relocation-recovery-required' -and @(Get-OwnedInstallRoots $after).Count -eq 2 -and (Test-Path -LiteralPath $legacyInstallRoot) -and (Test-Path -LiteralPath $relocatedInstallRoot) -and $script:trace.Count -eq 0) 'Runtime during relocation copy preserves both ownership roots and defers rollback deletion'
    $journal=Reset-Fixture;$before=Read-ConfigText $driverConfig;$script:startAfterTargetRegistration=$true;$refused=$false
    try{Invoke-OwnedRelocation $journal}catch{$refused=$true}
    $after=Read-Journal
    Assert ($refused -and $after.phase -eq 'relocation-recovery-required' -and $script:trace.Count -eq 1 -and $script:fakeRegistrations -contains $legacyInstallRoot -and $script:fakeRegistrations -contains $relocatedInstallRoot -and (Test-Path -LiteralPath $legacyInstallRoot) -and (Test-Path -LiteralPath $relocatedInstallRoot) -and (Read-ConfigText $driverConfig) -ceq $before) 'Runtime after target registration prevents source unregister and every rollback mutation'

    $journal=Reset-Fixture
    Remove-Item -LiteralPath $journalPath
    $script:fakeRegistrations=@();Sync-FixtureRegistrations;$before=Read-ConfigText $driverConfig;$script:stopWhenInstallCopied=$true;$refused=$false
    try{Invoke-FixtureAction 'Install'}catch{$refused=$true}
    $after=Read-Journal
    Assert ($refused -and $after.phase -eq 'install-recovery-required' -and $after.originalConfig -ceq $before -and $after.lastConfig -ceq $before -and (Test-Path -LiteralPath (Join-Path $relocatedInstallRoot 'second.txt')) -and (Read-ConfigText $driverConfig) -ceq $before -and $script:trace.Count -eq 0) 'Runtime after install copy retains prepared ownership without config or registration changes'
    $script:stopWhenInstallCopied=$false;Invoke-FixtureAction 'Uninstall'
    Assert ((Read-Journal).phase -eq 'uninstalled' -and !(Test-Path -LiteralPath $relocatedInstallRoot) -and (Read-ConfigText $driverConfig) -ceq $before) 'Interrupted fresh install can be uninstalled after the injected runtime stops'

    $journal=Reset-Fixture;$script:stopAfterRestoration=$true;$refused=$false
    try{Invoke-FixtureAction 'Uninstall'}catch{$refused=$true}
    $after=Read-Journal
    Assert ($refused -and $after.phase -eq 'uninstall-recovery-required' -and $after.lastConfig -ceq $script:originalText -and (Read-ConfigText $driverConfig) -ceq $script:originalText -and (Test-Path -LiteralPath $legacyInstallRoot) -and $script:trace.Count -eq 1) 'Uninstall records completed restoration and retains driver files when runtime appears before deletion'
    $script:stopAfterRestoration=$false;Invoke-FixtureAction 'Uninstall'
    Assert ((Read-Journal).phase -eq 'uninstalled' -and !(Test-Path -LiteralPath $legacyInstallRoot) -and (Read-ConfigText $driverConfig) -ceq $script:originalText) 'Interrupted uninstall resumes without a false config conflict after normal shutdown'

    foreach($kind in @('journal','config','current-directory','legacy-directory','current-registration','legacy-registration')){
        Reset-FreshFixture
        # Simulate state appearing after a previously clear GUI preflight.
        switch($kind){
            'journal' {Save-Journal ([pscustomobject]@{version=1;installRoot=$legacyInstallRoot;originalRegistration=$false;originalConfig=$null;lastConfig=$null;phase='uninstalled'})}
            'config' {[IO.File]::WriteAllText($driverConfig,'{"preserve":"existing-config"}')}
            'current-directory' {[IO.Directory]::CreateDirectory($relocatedInstallRoot)|Out-Null;[IO.File]::WriteAllText((Join-Path $relocatedInstallRoot 'foreign.txt'),'preserve-current')}
            'legacy-directory' {[IO.Directory]::CreateDirectory($legacyInstallRoot)|Out-Null;[IO.File]::WriteAllText((Join-Path $legacyInstallRoot 'foreign.txt'),'preserve-legacy')}
            'current-registration' {$script:fakeRegistrations=@($relocatedInstallRoot.ToUpperInvariant()+'\');Sync-FixtureRegistrations}
            'legacy-registration' {$script:fakeRegistrations=@($legacyInstallRoot);Sync-FixtureRegistrations}
        }
        $before=Get-FixtureFingerprint;$refused=$false
        try{Invoke-FixtureAction 'Install' -FreshSetupOnly}catch{$refused=$true}
        Assert ($refused -and (Get-FixtureFingerprint) -ceq $before -and $script:trace.Count -eq 0) "Fresh GUI Install preserves $kind introduced after GUI preflight"
    }
    Reset-FreshFixture
    [IO.Directory]::Delete($dataRoot,$false)
    $script:fakeRegistrations=@($legacyInstallRoot);Sync-FixtureRegistrations;$refused=$false
    try{Invoke-FixtureAction 'Install' -FreshSetupOnly}catch{$refused=$true}
    Assert ($refused -and !(Test-Path -LiteralPath $dataRoot) -and !(Test-Path -LiteralPath $journalPath)) 'Fresh registration conflict is rejected before creating the canonical config directory'

    Reset-FreshFixture;Invoke-FixtureAction 'Install' -FreshSetupOnly
    $after=Read-Journal
    Assert ($after.phase -eq 'installed' -and $after.freshSetupRootCreated -and $null -eq $after.originalConfig -and (Read-ConfigText $driverConfig) -ceq $after.lastConfig -and $script:fakeRegistrations -contains $relocatedInstallRoot -and (Read-ConfigText (Join-Path $relocatedInstallRoot 'second.txt')) -ceq 'second-owned-content') 'Fresh install creates and records only its exclusively created root, config and registration'

    foreach($kind in @('root','config','journal','file')){
        Reset-FreshFixture;$script:injectFreshState=$kind;$refused=$false
        try{Invoke-FixtureAction 'Install' -FreshSetupOnly}catch{$refused=$true}
        switch($kind){
            'root' {$after=Read-Journal;Assert ($refused -and !$after.freshSetupRootCreated -and @(Get-OwnedInstallRoots $after).Count -eq 0 -and (Read-ConfigText (Join-Path $relocatedInstallRoot 'foreign.txt')) -ceq 'foreign-root-preserved' -and $script:trace.Count -eq 0) 'Foreign directory racing exclusive creation is preserved and never recorded as cleanup-owned';Invoke-FixtureAction 'Uninstall';Assert ((Test-Path -LiteralPath (Join-Path $relocatedInstallRoot 'foreign.txt')) -and (Read-Journal).phase -eq 'uninstalled') 'Recovery never deletes the racing foreign directory'}
            'config' {Assert ($refused -and (Read-ConfigText $driverConfig) -ceq $script:injectedText -and $script:trace.Count -eq 0 -and (Read-Journal).phase -eq 'install-recovery-required') 'Foreign config appearing during fresh copy is preserved without registration'}
            'journal' {Assert ($refused -and (Read-ConfigText $journalPath) -ceq $script:injectedText -and $script:trace.Count -eq 0) 'Foreign journal appearing during fresh copy is never overwritten by recovery'}
            'file' {Assert ($refused -and (Read-ConfigText (Join-Path $relocatedInstallRoot 'second.txt')) -ceq $script:injectedText -and $script:trace.Count -eq 0) 'Foreign file racing a fresh copy is preserved by no-overwrite creation'}
        }
    }
    Reset-FreshFixture
    Save-Journal ([pscustomobject]@{version=1;installRoot=$legacyInstallRoot;originalRegistration=$false;originalConfig=$null;lastConfig=$null;phase='uninstalled'})
    Invoke-FixtureAction 'Install'
    Assert ((Read-Journal).phase -eq 'installed' -and @(Get-ChildItem -LiteralPath $dataRoot -Filter 'installation-journal-*.json').Count -eq 1) 'Legacy CLI Install retains deliberate completed-journal archival semantics'

    Reset-OwnedFixture;Invoke-FixtureAction 'Enable' -OwnedSetupOnly
    $config=Read-ConfigText $driverConfig|ConvertFrom-Json;$after=Read-Journal
    Assert ($config.experimentalOptIn -and $config.custom -ceq 'Keep-me' -and $after.phase -eq 'installed' -and $after.lastConfig -ceq (Read-ConfigText $driverConfig) -and $script:trace.Count -eq 0) 'Owned GUI Enable changes only flags on the complete matching disabled installation'
    foreach($kind in @('missing-journal','prepared-journal','uninstalled-journal','preexisting-registration','string-version','changed-config','changed-driver','missing-resource','extra-resource','other-owned-registration','missing-registration','not-owned-root','already-enabled')){
        Reset-OwnedFixture
        $journal=Read-Journal
        switch($kind){
            'missing-journal' {Remove-Item -LiteralPath $journalPath}
            'prepared-journal' {$journal.phase='prepared';Save-Journal $journal}
            'uninstalled-journal' {$journal.phase='uninstalled';Save-Journal $journal}
            'preexisting-registration' {$journal.originalRegistration=$true;Save-Journal $journal}
            'string-version' {$journal.version='1';Save-Journal $journal}
            'changed-config' {[IO.File]::WriteAllText($driverConfig,'{"foreign":"preserved","experimentalOptIn":false}')}
            'changed-driver' {[IO.File]::WriteAllText((Join-Path $relocatedInstallRoot 'bin/win64/driver_switcheroonie.dll'),'foreign-driver-preserved')}
            'missing-resource' {Remove-Item -LiteralPath (Join-Path $relocatedInstallRoot 'first.txt')}
            'extra-resource' {[IO.File]::WriteAllText((Join-Path $relocatedInstallRoot 'foreign.txt'),'foreign-resource-preserved')}
            'other-owned-registration' {$script:fakeRegistrations+=@($legacyInstallRoot);Sync-FixtureRegistrations}
            'missing-registration' {$script:fakeRegistrations=@();Sync-FixtureRegistrations}
            'not-owned-root' {$journal.freshSetupRootCreated=$false;Save-Journal $journal}
            'already-enabled' {$config=Read-ConfigText $driverConfig|ConvertFrom-Json;$config.experimentalOptIn=$true;[IO.File]::WriteAllText($driverConfig,($config|ConvertTo-Json));$journal.lastConfig=Read-ConfigText $driverConfig;Save-Journal $journal}
        }
        $before=Get-FixtureFingerprint;$refused=$false
        try{Invoke-FixtureAction 'Enable' -OwnedSetupOnly}catch{$refused=$true}
        Assert ($refused -and (Get-FixtureFingerprint) -ceq $before -and $script:trace.Count -eq 0) "Owned GUI Enable preserves $kind introduced after GUI preflight"
    }
    foreach($kind in @('config','journal','resource','registration')){
        Reset-OwnedFixture;$script:ownedRace=$kind;$before=Read-ConfigText $driverConfig;$refused=$false
        try{Invoke-FixtureAction 'Enable' -OwnedSetupOnly}catch{$refused=$true}
        $config=Read-ConfigText $driverConfig|ConvertFrom-Json
        Assert ($refused -and !$config.experimentalOptIn -and $script:trace.Count -eq 0) "Owned GUI Enable repeats $kind authority after the closing-inventory read and refuses before writing"
    }
    Reset-OwnedFixture;$authority=Get-OwnedSetupAuthority;$written=Write-DriverConfig $true
    $journal=Read-Journal;$journal.phase='foreign-conflict';Save-Journal $journal;$journalBefore=Read-ConfigText $journalPath;$refused=$false
    try{Assert-OwnedSetupAfterWrite $authority $written}catch{$refused=$true}
    Assert ($refused -and (Read-ConfigText $journalPath) -ceq $journalBefore) 'Owned journal-save boundary preserves an intervening journal after the flag write'
    $refused=$false;try{Save-Journal $journal -ExpectedText $authority.journalText}catch{$refused=$true}
    Assert ($refused -and (Read-ConfigText $journalPath) -ceq $journalBefore) 'Expected-text journal save independently refuses a stale authority snapshot'
    foreach($bound in @('files','directories','bytes')) {
        Reset-OwnedFixture
        switch($bound) {
            'files' {for($index=0;$index -lt 257;++$index){[IO.File]::WriteAllText((Join-Path $packageDriverRoot ('bound-{0}.txt' -f $index)),'')}}
            'directories' {for($index=0;$index -lt 257;++$index){[IO.Directory]::CreateDirectory((Join-Path $packageDriverRoot ('bound-{0}' -f $index)))|Out-Null}}
            'bytes' {$stream=[IO.File]::Open((Join-Path $packageDriverRoot 'oversized.bin'),[IO.FileMode]::CreateNew);try{$stream.SetLength(64MB+1)}finally{$stream.Dispose()}}
        }
        $refused=$false;try{$null=Get-DriverResourceInventory $packageDriverRoot}catch{$refused=$true}
        Assert $refused ('Resource inventory enforces its {0} bound without driver or runtime calls' -f $bound)
    }
    Reset-OwnedFixture;Remove-Item -LiteralPath $journalPath;Invoke-FixtureAction 'Enable'
    Assert (!(Test-Path -LiteralPath $journalPath) -and ((Read-ConfigText $driverConfig|ConvertFrom-Json).experimentalOptIn)) 'Legacy CLI Enable preserves its explicit behavior without the GUI-only ownership flag'
} catch {
    $exitCode=1
    if(!$results.Count -or $results[$results.Count-1].passed){$results.Add([pscustomobject]@{name=$_.Exception.Message;passed=$false})}
    Write-Output $_.Exception.Message
} finally {
    $resolved=[IO.Path]::GetFullPath($sandbox)
    if(!$resolved.StartsWith($sandboxBase+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe sandbox cleanup target.'}
    Assert-FixtureOrdinaryPath $resolved
    if(Test-Path -LiteralPath $resolved){
        foreach($item in @(Get-ChildItem -LiteralPath $resolved -Recurse -Force)){
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Fixture cleanup refused a redirected child.'}
        }
        Assert-FixtureOrdinaryPath $resolved
    }
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
    [pscustomobject]@{schemaVersion=1;dateUtc=[DateTime]::UtcNow.ToString('o');powershellVersion=$PSVersionTable.PSVersion.ToString();passed=($exitCode -eq 0);results=$results.ToArray();scope='Extracted installer functions and action dispatch, entirely injected process inventory and registration command, test-owned sandbox. No actual host process inventory, known folders, config, driver registration or runtime actions.';limitation='Fresh checks before each mutation cannot atomically prevent an unrelated runtime starting after the check.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $RepositoryRoot 'reports/installer-process-guard-regression.json') -Encoding UTF8
    Write-Output ('Installer process guard regression: {0} passed, {1} failed.' -f @($results|Where-Object passed).Count,@($results|Where-Object {!$_.passed}).Count)
}
exit $exitCode
