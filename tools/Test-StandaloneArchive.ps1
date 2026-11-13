param(
    [Parameter(Mandatory=$true)][string]$Archive,
    [Parameter(Mandatory=$true)][string]$Package,
    [string]$Manifest
)
$ErrorActionPreference = 'Stop'
$taskPackage = [IO.Path]::GetFullPath($Package).TrimEnd('\')
$taskArchive = [IO.Path]::GetFullPath($Archive)
$taskWrapperPath = 'portable/VRC-SWITCHEROONIE.exe'
$taskExpected = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
if ($Manifest) {
    # Publication calls the exact pinned-key verifier before this inventory check.
    $taskSigned = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
    $taskFiles = @($taskSigned.files)
} else {
    $taskFiles = @(Get-ChildItem -LiteralPath $taskPackage -File -Recurse | ForEach-Object {
        [pscustomobject]@{path=$_.FullName.Substring($taskPackage.Length + 1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
}
foreach ($taskFile in $taskFiles) {
    if ($taskExpected.ContainsKey('App/' + $taskFile.path)) { throw 'Duplicate standalone inventory.' }
    $taskExpected.Add('App/' + $taskFile.path,$taskFile)
}
$taskWrapper = @($taskFiles | Where-Object path -ceq $taskWrapperPath)
if ($taskWrapper.Count -ne 1) { throw 'A single signed standalone wrapper is required.' }
$taskExpected.Add('VRC-SWITCHEROONIE.exe',$taskWrapper[0])
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ((Get-Item -LiteralPath $taskArchive).Length -gt 512MB) { throw 'Standalone archive exceeds the download bound.' }
$taskSeen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$taskStream = [IO.File]::OpenRead($taskArchive)
try {
    $taskZip = New-Object IO.Compression.ZipArchive($taskStream,[IO.Compression.ZipArchiveMode]::Read,$true)
    try {
        if ($taskZip.Entries.Count -ne $taskExpected.Count) { throw 'Standalone archive file count differs from its complete inventory.' }
        foreach ($taskMember in $taskZip.Entries) {
            $taskName = $taskMember.FullName
            if (!$taskExpected.ContainsKey($taskName) -or !$taskSeen.Add($taskName)) { throw 'Standalone archive contains an unexpected or duplicate path.' }
            $taskItem = $taskExpected[$taskName]
            $taskCanonical = if ($taskName -ceq 'VRC-SWITCHEROONIE.exe') { $taskName } else { 'App/' + $taskItem.path }
            if ($taskName -cne $taskCanonical -or $taskMember.Length -ne $taskItem.bytes -or ((($taskMember.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000)) { throw 'Standalone archive has an unsafe alias, link or size.' }
            $taskData = $taskMember.Open()
            $taskAlgorithm = [Security.Cryptography.SHA256]::Create()
            try {
                $taskActual = ([BitConverter]::ToString($taskAlgorithm.ComputeHash($taskData))).Replace('-','')
                if ($taskActual -ine $taskItem.sha256) { throw 'Standalone archive file does not match its complete inventory.' }
            } finally { $taskAlgorithm.Dispose(); $taskData.Dispose() }
        }
    } finally { $taskZip.Dispose() }
} finally { $taskStream.Dispose() }
if ($taskSeen.Count -ne $taskExpected.Count) { throw 'Standalone archive is incomplete.' }
Write-Host ("Verified standalone archive: {0} files, exact wrapper and complete bundled payload." -f $taskSeen.Count)
