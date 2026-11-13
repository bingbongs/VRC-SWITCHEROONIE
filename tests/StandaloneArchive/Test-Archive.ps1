$ErrorActionPreference = 'Stop'
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskFixture = Join-Path $taskRepo ('build\standalone-archive-fixture-' + [Guid]::NewGuid().ToString('N'))
$taskPackage = Join-Path $taskFixture 'payload'
[IO.Directory]::CreateDirectory((Join-Path $taskPackage 'portable')) | Out-Null
[IO.File]::WriteAllBytes((Join-Path $taskPackage 'VRC-SWITCHEROONIE.exe'), [byte[]](1,2,3))
[IO.File]::WriteAllBytes((Join-Path $taskPackage 'portable\VRC-SWITCHEROONIE.exe'), [byte[]](4,5,6))
[IO.File]::WriteAllText((Join-Path $taskPackage 'bundled-runtime.dll'), 'fixture bundled runtime')
$taskFiles = @(Get-ChildItem -LiteralPath $taskPackage -File -Recurse | ForEach-Object {
    [pscustomobject]@{path=$_.FullName.Substring($taskPackage.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$taskFiles | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPackage 'package-hashes.json') -Encoding UTF8
$taskArchive = Join-Path $taskFixture 'portable.zip'
$taskCreate = Join-Path $taskRepo 'tools\New-StandaloneArchive.ps1'
$taskVerify = Join-Path $taskRepo 'tools\Test-StandaloneArchive.ps1'
$taskPassed = 0
function Check([bool]$Condition,[string]$Name) { if (!$Condition) { throw ('FAIL: ' + $Name) }; $script:taskPassed++ }
function Refused([scriptblock]$Action,[string]$Name) {
    $taskRefused = $false
    try { & $Action } catch { $taskRefused = $true }
    Check $taskRefused $Name
}
& $taskCreate -Package $taskPackage -Output $taskArchive
Check (Test-Path -LiteralPath $taskArchive) 'complete portable archive created'
Refused { & $taskCreate -Package $taskPackage -Output $taskArchive } 'existing archive never overwritten'
$taskManifest = Join-Path $taskFixture 'inventory.json'
$taskInventory = @(Get-ChildItem -LiteralPath $taskPackage -File -Recurse | ForEach-Object {
    [pscustomobject]@{path=$_.FullName.Substring($taskPackage.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
@{files=$taskInventory} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskManifest -Encoding UTF8
& $taskVerify -Archive $taskArchive -Package $taskPackage -Manifest $taskManifest
Check $true 'root wrapper and all App entries match receipt inventory'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($taskAttack in @('dependency','wrapper','extra','missing','alias','duplicate')) {
    $taskBad = Join-Path $taskFixture ($taskAttack + '.zip')
    $taskRead = [IO.Compression.ZipFile]::OpenRead($taskArchive)
    $taskWrite = [IO.Compression.ZipFile]::Open($taskBad,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($taskEntry in $taskRead.Entries) {
            if ($taskAttack -eq 'missing' -and $taskEntry.FullName -ceq 'App/bundled-runtime.dll') { continue }
            $taskName = $taskEntry.FullName
            if ($taskAttack -eq 'alias' -and $taskName -ceq 'App/bundled-runtime.dll') { $taskName = 'app/bundled-runtime.dll' }
            $taskMember = $taskWrite.CreateEntry($taskName)
            $taskSource = $taskEntry.Open(); $taskDest = $taskMember.Open()
            try {
                if (($taskAttack -eq 'dependency' -and $taskName -ceq 'App/bundled-runtime.dll') -or
                    ($taskAttack -eq 'wrapper' -and $taskName -ceq 'VRC-SWITCHEROONIE.exe')) {
                    $taskBytes = New-Object byte[] ([int]$taskEntry.Length)
                    $taskDest.Write($taskBytes,0,$taskBytes.Length)
                } else { $taskSource.CopyTo($taskDest) }
            } finally { $taskDest.Dispose(); $taskSource.Dispose() }
        }
        if ($taskAttack -eq 'extra' -or $taskAttack -eq 'duplicate') {
            $taskName = if ($taskAttack -eq 'extra') {'App/foreign.exe'} else {'App/bundled-runtime.dll'}
            $taskMember = $taskWrite.CreateEntry($taskName); $taskDest = $taskMember.Open(); $taskDest.Dispose()
        }
    } finally { $taskWrite.Dispose(); $taskRead.Dispose() }
    Refused { & $taskVerify -Archive $taskBad -Package $taskPackage -Manifest $taskManifest } ('refuse ' + $taskAttack + ' archive')
}
[IO.File]::AppendAllText((Join-Path $taskPackage 'bundled-runtime.dll'), 'changed')
Refused { & $taskCreate -Package $taskPackage -Output (Join-Path $taskFixture 'changed.zip') } 'changed frozen source refuses packaging'
Write-Host ("{0} standalone archive checks passed; fixture files only." -f $taskPassed)
exit 0
