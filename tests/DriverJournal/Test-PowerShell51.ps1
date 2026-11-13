param([string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')))
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Run this regression test with Windows PowerShell 5.1.' }
$scriptPath = Join-Path $RepositoryRoot 'tools/Manage-Driver.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Installer script did not parse in Windows PowerShell 5.1.' }
$functionNames = @('Read-ConfigText','Convert-JournalConfigText','Read-Journal','Get-JournalSerializedText','Save-Journal','Write-DriverConfig','Restore-DriverConfig')
foreach ($name in $functionNames) {
    $function = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name}, $true)
    if ($null -eq $function) { throw "Missing installer function $name" }
    # Only compile owned function definitions. Never execute installation/registration top-level code.
    Invoke-Expression $function.Extent.Text
}
$sandboxBase = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'sandbox'))
$sandbox = Join-Path $sandboxBase ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$driverConfig = Join-Path $sandbox 'driver.json'
$journalPath = Join-Path $sandbox 'installation-journal.json'
$results = New-Object 'System.Collections.Generic.List[object]'
$exitCode = 0
# The extracted config functions call this injected guard, never the host inventory.
function Assert-SetupStopped { }
function Assert([bool]$Condition, [string]$Name) {
    $results.Add([pscustomobject]@{name=$Name;passed=$Condition})
    if (!$Condition) { throw "FAILED: $Name" }
    Write-Output "PASS $Name"
}
function Write-Exact([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false))) }
function Expect-ReadRejection($Value, [string]$Name) {
    $before = [string](Read-ConfigText $driverConfig)
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $journalPath -Encoding UTF8
    $rejected = $false
    try { $null = Read-Journal } catch { $rejected = $true }
    Assert $rejected $Name
    Assert ((Read-ConfigText $driverConfig) -ceq $before) "$Name leaves configuration unchanged"
}
try {
    # Includes CRLF, escaped values, Unicode, and a trailing newline to check exact text preservation.
    $accent = [char]0x00E9
    $original = '{"custom":"Pr' + $accent + 'serve-A","experimentalOptIn":false,"approvedRuntimeBuild":"old","escaped":"a\\b\"c"}' + "`r`n"
    Write-Exact $driverConfig $original
    $originalConfig = [string](Read-ConfigText $driverConfig)
    Assert ($originalConfig -is [string] -and $originalConfig -ceq $original) 'Original backup is exact plain CLR text'
    $written = [string](Write-DriverConfig $true)
    $config = $written | ConvertFrom-Json
    Assert ($written -is [string] -and $config.experimentalOptIn -eq $true -and $config.approvedRuntimeBuild -eq '25330290') 'Write-DriverConfig returns plain configuration text'
    Assert ($config.custom -ceq ('Pr' + $accent + 'serve-A')) 'Writing owned flags preserves existing Unicode fields'
    $journal = [pscustomobject]@{version=1;installRoot='private sandbox';originalRegistration=$false;originalConfig=$originalConfig;lastConfig=[string]$written;phase='installed'}
    Save-Journal $journal
    $serialized = [string](Read-ConfigText $journalPath)
    $parsed = $serialized | ConvertFrom-Json
    Assert ($parsed.originalConfig -is [string] -and $parsed.lastConfig -is [string]) 'Windows PowerShell journal stores text strings rather than decorated objects'
    Assert ($parsed.originalConfig -ceq $original -and $parsed.lastConfig -ceq $written) 'Journal round-trip preserves exact backup and last-written text'
    Assert ((Get-Item -LiteralPath $journalPath).Length -lt 8192 -and $serialized -notmatch 'PSPath|PSProvider|PSParentPath') 'Journal remains compact and contains no Get-Content provider metadata'

    $legacy = [pscustomobject]@{version=1;installRoot='private sandbox';originalRegistration=$false;originalConfig=[pscustomobject]@{value=$original;PSPath='private-test-metadata';PSProvider=[pscustomobject]@{name='FileSystem'}};lastConfig=[pscustomobject]@{value=$written;PSPath='private-test-metadata';Length=$written.Length};phase='installed'}
    $legacy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $journalPath -Encoding UTF8
    $normalized = Read-Journal
    Assert ($normalized.originalConfig -is [string] -and $normalized.originalConfig -ceq $original -and $normalized.lastConfig -is [string] -and $normalized.lastConfig -ceq $written) 'Legacy value wrappers normalize to exact configuration strings'
    Restore-DriverConfig $normalized
    Assert ((Read-ConfigText $driverConfig) -ceq $original) 'Legacy recovery restores original Unicode, escapes, and line endings exactly'
    Save-Journal $normalized
    Assert ((Read-ConfigText $journalPath) -notmatch 'PSPath|PSProvider|PSParentPath') 'Saving normalized legacy journal removes provider metadata'

    Write-Exact $driverConfig $written.Replace('serve-A','serve-a')
    $conflictText = [string](Read-ConfigText $driverConfig)
    $conflict = $false
    try { Restore-DriverConfig $normalized } catch { $conflict = $true }
    Assert ($conflict -and (Read-ConfigText $driverConfig) -ceq $conflictText) 'Case-only external configuration change is retained and blocks restoration'

    $invalid = [pscustomobject]@{originalConfig=$null;lastConfig=[pscustomobject]@{value=42}}
    Expect-ReadRejection $invalid 'Numeric legacy value rejected'
    $invalid.lastConfig = [pscustomobject]@{value='invalid JSON'}
    Expect-ReadRejection $invalid 'Invalid JSON legacy value rejected'
    $invalid.lastConfig = [pscustomobject]@{value='[]'}
    Expect-ReadRejection $invalid 'Legacy array value rejected'
    Expect-ReadRejection ([pscustomobject]@{originalConfig=$null}) 'Missing lastConfig rejected'

    Write-Exact $driverConfig $written
    $withoutOriginal = [pscustomobject]@{originalConfig=$null;lastConfig=$written}
    Save-Journal $withoutOriginal
    $nullBackup = Read-Journal
    Assert ($null -eq $nullBackup.originalConfig) 'Null original configuration remains null through journal normalization'
    Restore-DriverConfig $nullBackup
    Assert (!(Test-Path -LiteralPath $driverConfig)) 'No-original restoration removes only the private sandbox configuration'
} catch {
    $exitCode = 1
    if (!$results.Count -or $results[$results.Count - 1].passed) { $results.Add([pscustomobject]@{name=$_.Exception.Message;passed=$false}) }
    Write-Output $_.Exception.Message
} finally {
    # Checked, absolute, test-owned target; no live installer paths are used anywhere in this test.
    $resolvedSandbox = [IO.Path]::GetFullPath($sandbox)
    if (!$resolvedSandbox.StartsWith($sandboxBase + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe sandbox cleanup path.' }
    if (Test-Path -LiteralPath $resolvedSandbox) { Remove-Item -LiteralPath $resolvedSandbox -Recurse -Force }
    $reportPath = Join-Path $RepositoryRoot 'reports/driver-journal-regression.json'
    [pscustomobject]@{schemaVersion=1;dateUtc=[DateTime]::UtcNow.ToString('o');powershellVersion=$PSVersionTable.PSVersion.ToString();passed=($exitCode -eq 0);results=$results.ToArray();scope='Extracted owned installer functions in a private sandbox. No driver registration, live installation, runtime, application, ACL, or configuration changes.'} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Output ("Windows PowerShell journal regression: {0} passed, {1} failed. Report: reports/driver-journal-regression.json" -f @($results | Where-Object passed).Count,@($results | Where-Object {!$_.passed}).Count)
}
exit $exitCode
