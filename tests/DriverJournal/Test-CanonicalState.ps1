param([string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')))
$ErrorActionPreference = 'Stop'
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepositoryRoot 'tools/Manage-Driver.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Installer does not parse.'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ProcessKnownFolderPath'},$true)
if($null -eq $definition){throw 'Missing process-token directory resolver.'}
# Extract resolver only; never run installer top-level registration/runtime/config actions.
Invoke-Expression $definition.Extent.Text
$results=New-Object 'System.Collections.Generic.List[object]'
$exitCode=0
function Assert([bool]$Condition,[string]$Name){$results.Add([pscustomobject]@{name=$Name;passed=$Condition});if(!$Condition){throw "FAILED: $Name"};Write-Output "PASS $Name"}
function Invoke-PathAssignments {
    foreach($name in 'profileRoot','localAppDataRoot','dataRoot','legacyDataRoot','legacyInstallRoot','relocatedInstallRoot','allowedOwnedRoots','installRoot','driverConfig','journalPath'){
        $assignment=$ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left -is [Management.Automation.Language.VariableExpressionAst] -and $_.Left.VariablePath.UserPath -eq $name} | Select-Object -First 1
        if($null -eq $assignment){throw "Missing state assignment $name"}
        Invoke-Expression $assignment.Extent.Text
    }
    return [pscustomobject]@{config=$dataRoot;driver=$driverConfig;journal=$journalPath;legacy=$legacyInstallRoot;install=$installRoot;allowed=$allowedOwnedRoots}
}
$priorProfile=$env:USERPROFILE;$priorLocal=$env:LOCALAPPDATA
try {
    $folder=[Guid]'5E6C858F-0E22-4760-9AFE-EA3317B67173'
    $expected=Get-ProcessKnownFolderPath $folder
    $env:USERPROFILE='Z:\Untrusted-Environment-Profile';$env:LOCALAPPDATA='Z:\Untrusted-Environment-AppData'
    Assert ((Get-ProcessKnownFolderPath $folder) -ceq $expected) 'Process-token resolver ignores USERPROFILE and LOCALAPPDATA overrides'
    $actual=Invoke-PathAssignments
    Assert ($actual.config -eq (Join-Path $expected 'VRC-SWITCHEROONIE\config')) 'Installer configuration authority matches the native Profile/config layout'
    Assert ($actual.driver -eq (Join-Path $actual.config 'driver.json') -and $actual.journal -eq (Join-Path $actual.config 'installation-journal.json')) 'Driver and installation journal share only the canonical config directory'
    Assert ($actual.install -eq (Join-Path $expected 'VRC-SWITCHEROONIE\drivers\0.1.0\switcheroonie')) 'Existing Profile driver registration path is unchanged'
    Assert ($actual.allowed.Count -eq 2 -and $actual.allowed -contains $actual.legacy -and $actual.allowed -contains $actual.install -and $actual.legacy -ne $actual.install) 'Historical LocalAppData driver ownership remains a separate exact allowlist entry'
    Assert ($actual.config -ne ([IO.Path]::Combine($env:LOCALAPPDATA,'VRC-SWITCHEROONIE')) -and $actual.config -ne ([IO.Path]::Combine($env:USERPROFILE,'VRC-SWITCHEROONIE\config'))) 'Environment paths never become installer configuration authority'
    # Fake folder API: only pure assignment evaluation; no filesystem or registration operations.
    function Get-ProcessKnownFolderPath([Guid]$Folder) {
        if($Folder -eq [Guid]'5E6C858F-0E22-4760-9AFE-EA3317B67173'){return 'C:\Fixture Profile'}
        if($Folder -eq [Guid]'F1B32785-6FBA-4FCF-9D55-7B8E7F157091'){return 'C:\Fixture Legacy'}
        throw 'Unexpected folder query.'
    }
    $fixture=Invoke-PathAssignments
    Assert ($fixture.driver -eq 'C:\Fixture Profile\VRC-SWITCHEROONIE\config\driver.json' -and $fixture.journal -eq 'C:\Fixture Profile\VRC-SWITCHEROONIE\config\installation-journal.json') 'Space-containing profile fixture resolves both active files deterministically'
    Assert ($fixture.legacy -eq 'C:\Fixture Legacy\VRC-SWITCHEROONIE\drivers\0.1.0\switcheroonie' -and $fixture.install -eq 'C:\Fixture Profile\VRC-SWITCHEROONIE\drivers\0.1.0\switcheroonie') 'Legacy cleanup and current installation roots cannot become config fallbacks'
    function Get-ProcessKnownFolderPath([Guid]$Folder) {throw 'Injected process-token folder resolution failure.'}
    $refused=$false;try{$null=Invoke-PathAssignments}catch{$refused=$true}
    Assert $refused 'Resolver failure stops before any environment or working-directory fallback'
} catch {
    $exitCode=1
    if(!$results.Count -or $results[$results.Count-1].passed){$results.Add([pscustomobject]@{name=$_.Exception.Message;passed=$false})}
    Write-Output $_.Exception.Message
} finally {
    $env:USERPROFILE=$priorProfile;$env:LOCALAPPDATA=$priorLocal
    [pscustomobject]@{schemaVersion=1;dateUtc=[DateTime]::UtcNow.ToString('o');powershellVersion=$PSVersionTable.PSVersion.ToString();passed=($exitCode -eq 0);results=$results.ToArray();scope='Extracted resolver plus pure installer assignment fixtures. Known-folder reads only, no live config/journal/driver/registration/runtime/application changes.'} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RepositoryRoot 'reports/installer-canonical-state-regression.json') -Encoding UTF8
    Write-Output ('Canonical state regression: {0} passed, {1} failed.' -f @($results | Where-Object passed).Count,@($results | Where-Object {!$_.passed}).Count)
}
exit $exitCode
