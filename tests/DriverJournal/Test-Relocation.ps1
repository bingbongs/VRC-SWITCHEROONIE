param([string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')))
$ErrorActionPreference='Stop'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepositoryRoot 'tools/Manage-Driver.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Installer does not parse.'}
foreach($name in @('Get-ProcessKnownFolderPath','Read-ConfigText','Convert-JournalConfigText','Read-Journal','Save-Journal','Resolve-OwnedInstallRoot','Get-OwnedInstallRoots','Remove-OwnedInstallRoot','Assert-ConfigUnchanged','Invoke-OwnedRelocation')) {
    $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    if($null -eq $definition){throw "Missing owned function $name"}
    Invoke-Expression $definition.Extent.Text
}
$sandboxBase=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'sandbox'))
$sandbox=Join-Path $sandboxBase ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$legacyInstallRoot=Join-Path $sandbox 'legacy';$relocatedInstallRoot=Join-Path $sandbox 'proposed'
$allowedOwnedRoots=@($legacyInstallRoot,$relocatedInstallRoot)
$packageDriverRoot=Join-Path $sandbox 'latest-package-driver'
$driverConfig=Join-Path $sandbox 'driver.json';$journalPath=Join-Path $sandbox 'journal.json'
$foreignRoot=Join-Path $sandbox 'unrelated-driver'
$results=New-Object 'System.Collections.Generic.List[object]'
$exitCode=0;$script:fakeRegistrations=@();$script:failure='';$script:trace=@()
function Assert([bool]$Condition,[string]$Name){$results.Add([pscustomobject]@{name=$Name;passed=$Condition});if(!$Condition){throw "FAILED: $Name"};Write-Output "PASS $Name"}
function Get-RegisteredDriverRoots{return @($script:fakeRegistrations)}
function Set-DriverRegistration([string]$Operation,[string]$Root){
    $Root=Resolve-OwnedInstallRoot $Root
    $script:trace+=@("$Operation|$Root")
    if($script:failure -eq 'target-add' -and $Operation -eq 'adddriver' -and $Root -eq $relocatedInstallRoot){throw 'Injected target registration failure'}
    if($Operation -eq 'adddriver'){$script:fakeRegistrations=@($script:fakeRegistrations+$Root | Select-Object -Unique)}
    else{$script:fakeRegistrations=@($script:fakeRegistrations | Where-Object {$_ -ne $Root})}
    if($script:failure -eq 'source-remove-after-mutation' -and $Operation -eq 'removedriver' -and $Root -eq $legacyInstallRoot){throw 'Injected partial source unregister failure'}
}
function Reset-Fixture{
    foreach($root in $allowedOwnedRoots){Remove-OwnedInstallRoot $root}
    New-Item -ItemType Directory -Path $legacyInstallRoot,$packageDriverRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $legacyInstallRoot 'driver.vrdrivermanifest'),'{}')
    [IO.File]::WriteAllText((Join-Path $legacyInstallRoot 'legacy-rollback.txt'),'old-owned-tree')
    [IO.File]::WriteAllText((Join-Path $packageDriverRoot 'driver.vrdrivermanifest'),'{"name":"switcheroonie","directory":"","priority":20000}')
    [IO.File]::WriteAllText((Join-Path $packageDriverRoot 'driver.dll'),'latest-owned-binary')
    [IO.File]::WriteAllText($driverConfig,'{"experimentalOptIn":true,"custom":"Preserve-A"}')
    $script:fakeRegistrations=@($legacyInstallRoot,$foreignRoot);$script:failure='';$script:trace=@()
    $journal=[pscustomobject]@{version=1;installRoot=$legacyInstallRoot;originalRegistration=$false;originalConfig=$null;lastConfig=[string](Read-ConfigText $driverConfig);phase='installed'}
    Save-Journal $journal
    return $journal
}
try{
    $defaultSelection=& {
        foreach($name in 'profileRoot','localAppDataRoot','dataRoot','legacyDataRoot','legacyInstallRoot','relocatedInstallRoot','allowedOwnedRoots','installRoot'){
            $assignment=$ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left -is [Management.Automation.Language.VariableExpressionAst] -and $_.Left.VariablePath.UserPath -eq $name} | Select-Object -First 1
            if($null -eq $assignment){throw "Missing default root assignment $name"}
            Invoke-Expression $assignment.Extent.Text
        }
        [pscustomobject]@{selected=$installRoot;legacy=$legacyInstallRoot;relocated=$relocatedInstallRoot}
    }
    $expectedDefault=[IO.Path]::GetFullPath((Join-Path (Get-ProcessKnownFolderPath ([Guid]'5E6C858F-0E22-4760-9AFE-EA3317B67173')) 'VRC-SWITCHEROONIE\drivers\0.1.0\switcheroonie'))
    Assert ($defaultSelection.selected -eq $expectedDefault -and $defaultSelection.selected -eq $defaultSelection.relocated -and $defaultSelection.selected -ne $defaultSelection.legacy) 'Fresh default resolves to the user-profile owned root outside legacy LocalAppData'
    $installBlock=$ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -eq '$Action -eq ''Install'''} | Select-Object -First 1
    if($null -eq $installBlock){throw 'Missing Install action branch'}
    $installAssignment=$installBlock.Clauses[0].Item2.Statements | Where-Object {$_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left -is [Management.Automation.Language.VariableExpressionAst] -and $_.Left.VariablePath.UserPath -eq 'installRoot'} | Select-Object -First 1
    if($null -eq $installAssignment){throw 'Missing Install root selection'}
    $installSelection=& { $installRoot=$legacyInstallRoot;Invoke-Expression $installAssignment.Extent.Text;return $installRoot }
    Assert ($installSelection -eq $relocatedInstallRoot -and $installSelection -ne $legacyInstallRoot) 'Install action resets a completed legacy journal to the new default root'

    $journal=Reset-Fixture
    $before=[string](Read-ConfigText $driverConfig)
    Invoke-OwnedRelocation $journal
    $after=Read-Journal
    Assert ($after.installRoot -eq $relocatedInstallRoot -and $after.phase -eq 'installed') 'Relocation journal commits only the proposed allowlisted root'
    Assert ((Test-Path -LiteralPath $legacyInstallRoot) -and (Test-Path -LiteralPath $relocatedInstallRoot)) 'Legacy owned tree retained alongside relocated tree'
    Assert ((Read-ConfigText (Join-Path $relocatedInstallRoot 'driver.dll')) -ceq 'latest-owned-binary') 'Latest packaged driver resources take precedence over rollback tree'
    $manifest=(Read-ConfigText (Join-Path $relocatedInstallRoot 'driver.vrdrivermanifest')) | ConvertFrom-Json
    Assert ($null -eq $manifest.PSObject.Properties['directory'] -and $manifest.priority -eq 20000) 'Empty directory declaration removed while latest priority survives'
    Assert (@($script:fakeRegistrations).Count -eq 2 -and $script:fakeRegistrations -contains $relocatedInstallRoot -and $script:fakeRegistrations -contains $foreignRoot -and $script:fakeRegistrations -notcontains $legacyInstallRoot) 'Registration transition preserves unrelated provider'
    Assert ((Read-ConfigText $driverConfig) -ceq $before) 'Relocation leaves driver configuration text unchanged'
    Assert (@(Get-OwnedInstallRoots $after).Count -eq 2) 'Journal records both exact owned roots for eventual uninstall'
    $outsideRejected=$false;try{$null=Resolve-OwnedInstallRoot $foreignRoot}catch{$outsideRejected=$true}
    Assert $outsideRejected 'Foreign root rejected by exact allowlist'

    $journal=Reset-Fixture
    New-Item -ItemType Directory -Path $relocatedInstallRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $relocatedInstallRoot 'unknown.txt'),'preserve-unknown')
    $blocked=$false;try{Invoke-OwnedRelocation $journal}catch{$blocked=$true}
    Assert ($blocked -and (Read-ConfigText (Join-Path $relocatedInstallRoot 'unknown.txt')) -ceq 'preserve-unknown' -and $script:trace.Count -eq 0) 'Pre-existing proposed directory refused before registration or overwrite'

    $journal=Reset-Fixture
    [IO.File]::WriteAllText($driverConfig,$journal.lastConfig.Replace('Preserve-A','Preserve-a'))
    $blocked=$false;try{Invoke-OwnedRelocation $journal}catch{$blocked=$true}
    Assert ($blocked -and !(Test-Path -LiteralPath $relocatedInstallRoot) -and $script:trace.Count -eq 0) 'External case-only configuration change blocks relocation before copy or registration'

    foreach($mode in 'target-add','source-remove-after-mutation'){
        $journal=Reset-Fixture;$script:failure=$mode
        $blocked=$false;try{Invoke-OwnedRelocation $journal}catch{$blocked=$true}
        $after=Read-Journal
        Assert ($blocked -and $after.installRoot -eq $legacyInstallRoot -and $after.phase -eq 'installed') "$mode restores the prior journal"
        Assert ((Test-Path -LiteralPath $legacyInstallRoot) -and !(Test-Path -LiteralPath $relocatedInstallRoot)) "$mode retains source and removes only newly owned target"
        Assert ($script:fakeRegistrations -contains $legacyInstallRoot -and $script:fakeRegistrations -contains $foreignRoot -and $script:fakeRegistrations -notcontains $relocatedInstallRoot) "$mode restores source registration while preserving foreign provider"
    }
}catch{$exitCode=1;if(!$results.Count -or $results[$results.Count-1].passed){$results.Add([pscustomobject]@{name=$_.Exception.Message;passed=$false})};Write-Output $_.Exception.Message}
finally{
    $resolved=[IO.Path]::GetFullPath($sandbox)
    if(!$resolved.StartsWith($sandboxBase+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe sandbox cleanup target.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
    [pscustomobject]@{schemaVersion=1;dateUtc=[DateTime]::UtcNow.ToString('o');powershellVersion=$PSVersionTable.PSVersion.ToString();passed=($exitCode -eq 0);results=$results.ToArray();scope='Extracted relocation helpers, fake registration list, and test-owned sandbox. No live installation, runtime, registration, ACL, or application modified.'} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RepositoryRoot 'reports/driver-relocation-regression.json') -Encoding UTF8
    Write-Output ('Driver relocation regression: {0} passed, {1} failed.' -f @($results | Where-Object passed).Count,@($results | Where-Object {!$_.passed}).Count)
}
exit $exitCode
