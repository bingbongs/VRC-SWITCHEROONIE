param(
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$Output
)
$ErrorActionPreference = 'Stop'
$taskPackage = [IO.Path]::GetFullPath($Package).TrimEnd('\')
$taskOutput = [IO.Path]::GetFullPath($Output)
$taskWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
function Assert-OrdinaryPath([string]$Path) {
    # The explicitly selected workspace may be under a cloud-managed ancestor.
    # Refuse links throughout our source/output tree, including the workspace
    # itself, without treating an outside ancestor as an archive member.
    $taskInsideWorkspace = [IO.Path]::GetFullPath($Path).StartsWith($taskWorkspace + '\',[StringComparison]::OrdinalIgnoreCase)
    for ($taskCursor = [IO.Path]::GetFullPath($Path); $null -ne $taskCursor; $taskCursor = [IO.Path]::GetDirectoryName($taskCursor)) {
        if ((Test-Path -LiteralPath $taskCursor) -and ((Get-Item -LiteralPath $taskCursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Standalone packaging refuses reparse points.'
        }
        if ($taskInsideWorkspace -and $taskCursor.Equals($taskWorkspace,[StringComparison]::OrdinalIgnoreCase)) { break }
    }
}
function Assert-RelativePath([string]$Path) {
    if ([string]::IsNullOrEmpty($Path) -or $Path.StartsWith('/') -or $Path.Contains('\') -or $Path -match '[:*?"<>|\x00-\x1f]') { throw 'Unsafe standalone archive path.' }
    foreach ($taskPart in $Path.Split('/')) {
        if ($taskPart -in @('','.','..') -or $taskPart.EndsWith('.') -or $taskPart.EndsWith(' ') -or $taskPart.Split('.')[0] -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') { throw 'Unsafe standalone archive component.' }
    }
}
Assert-OrdinaryPath $taskPackage
Assert-OrdinaryPath $taskOutput
if (!(Test-Path -LiteralPath $taskPackage -PathType Container) -or (Test-Path -LiteralPath $taskOutput)) { throw 'Use a complete immutable package and a new standalone archive path.' }
if ($taskOutput.StartsWith($taskPackage + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'The standalone archive must be outside its payload.' }
$taskWrapper = Join-Path $taskPackage 'portable\VRC-SWITCHEROONIE.exe'
$taskLauncher = Join-Path $taskPackage 'VRC-SWITCHEROONIE.exe'
if (!(Test-Path -LiteralPath $taskWrapper -PathType Leaf) -or !(Test-Path -LiteralPath $taskLauncher -PathType Leaf)) { throw 'Standalone wrapper and managed launcher are required.' }
$taskInventoryPath = Join-Path $taskPackage 'package-hashes.json'
$taskInventory = Get-Content -LiteralPath $taskInventoryPath -Raw | ConvertFrom-Json
$taskExpected = New-Object 'Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($taskItem in $taskInventory) {
    $taskRelative = ([string]$taskItem.path).Replace('\','/')
    Assert-RelativePath $taskRelative
    if ($taskItem.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $taskExpected.ContainsKey($taskRelative)) { throw 'Standalone package inventory is invalid.' }
    $taskExpected.Add($taskRelative,[string]$taskItem.sha256)
}
$taskEntries = New-Object 'Collections.Generic.List[object]'
$taskEntries.Add([pscustomobject]@{name='VRC-SWITCHEROONIE.exe';source=$taskWrapper})
$taskDirectories = New-Object 'Collections.Generic.Stack[string]'
$taskDirectories.Push($taskPackage)
$taskSeen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
while ($taskDirectories.Count -gt 0) {
    $taskDirectory = $taskDirectories.Pop()
    foreach ($taskItem in @(Get-ChildItem -LiteralPath $taskDirectory -Force)) {
        Assert-OrdinaryPath $taskItem.FullName
        if ($taskItem.PSIsContainer) { $taskDirectories.Push($taskItem.FullName); continue }
        $taskRelative = $taskItem.FullName.Substring($taskPackage.Length + 1).Replace('\','/')
        Assert-RelativePath $taskRelative
        if (!$taskSeen.Add($taskRelative)) { throw 'Duplicate standalone file path.' }
        if ($taskRelative -ne 'package-hashes.json') {
            if (!$taskExpected.ContainsKey($taskRelative) -or (Get-FileHash -LiteralPath $taskItem.FullName -Algorithm SHA256).Hash -ine $taskExpected[$taskRelative]) { throw 'Standalone source no longer matches its frozen inventory.' }
        }
        $taskEntries.Add([pscustomobject]@{name=('App/' + $taskRelative);source=$taskItem.FullName})
    }
}
if ($taskSeen.Count -ne $taskExpected.Count + 1 -or @($taskExpected.Keys | Where-Object {!$taskSeen.Contains($_)}).Count -ne 0) { throw 'Standalone source inventory is incomplete.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskParent = [IO.Path]::GetDirectoryName($taskOutput)
[IO.Directory]::CreateDirectory($taskParent) | Out-Null
$taskTemporary = Join-Path $taskParent ('.standalone-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $taskStream = [IO.File]::Open($taskTemporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $taskZip = New-Object IO.Compression.ZipArchive($taskStream,[IO.Compression.ZipArchiveMode]::Create,$true)
        try {
            foreach ($taskEntry in @($taskEntries | Sort-Object name)) {
                $taskMember = $taskZip.CreateEntry($taskEntry.name,[IO.Compression.CompressionLevel]::Optimal)
                $taskSource = [IO.File]::OpenRead($taskEntry.source)
                $taskDestination = $taskMember.Open()
                try { $taskSource.CopyTo($taskDestination) }
                finally { $taskDestination.Dispose(); $taskSource.Dispose() }
            }
        } finally { $taskZip.Dispose() }
        $taskStream.Flush($true)
    } finally { $taskStream.Dispose() }
    Assert-OrdinaryPath $taskOutput
    if (Test-Path -LiteralPath $taskOutput) { throw 'Standalone output appeared during packaging; retained.' }
    [IO.File]::Move($taskTemporary,$taskOutput)
} finally {
    if (Test-Path -LiteralPath $taskTemporary) { Remove-Item -LiteralPath $taskTemporary }
}
Write-Host ("Standalone archive: {0} files; one root EXE plus App/." -f $taskEntries.Count)
& (Join-Path $PSScriptRoot 'Test-StandaloneArchive.ps1') -Archive $taskOutput -Package $taskPackage
