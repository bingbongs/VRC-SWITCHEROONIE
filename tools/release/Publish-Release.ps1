param(
    [Parameter(Mandatory=$true)][string]$Artifacts,
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$NotesFile
)
$ErrorActionPreference = 'Stop'
$artifactsPath = [IO.Path]::GetFullPath($Artifacts)
$manifestFile = Join-Path $artifactsPath 'release-manifest.json'
$signatureFile = Join-Path $artifactsPath 'release-signature.bin'
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
if ($manifest.repository -cne 'bingbongs/VRC-SWITCHEROONIE' -or $manifest.version -notmatch '^(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})$') { throw 'Release repository/version identity does not match this project.' }
$expectedName = "VRC-SWITCHEROONIE-$($manifest.version)-win-x64.zip"
if ($manifest.archiveName -cne $expectedName) { throw 'Unexpected release archive name.' }
$archive = Join-Path $artifactsPath $expectedName
if ((Get-Item -LiteralPath $signatureFile).Length -ne 64 -or (Get-Item -LiteralPath $archive).Length -ne $manifest.archiveBytes -or
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $manifest.archiveSha256) { throw 'Prepared release asset size/hash validation failed.' }
if (!(Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw 'A reviewable release notes file is required.' }
$helper = Join-Path ([IO.Path]::GetFullPath($Package)) 'Switcheroonie.Updater.exe'
if (!(Test-Path -LiteralPath $helper -PathType Leaf) -or $artifactsPath.Contains('"')) { throw 'An exact reviewed package verifier is required.' }
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $helper
$start.Arguments = '--verify-release "' + $artifactsPath.TrimEnd('\') + '"'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$verifier = [Diagnostics.Process]::Start($start)
try {
    $stdout = $verifier.StandardOutput.ReadToEndAsync()
    $stderr = $verifier.StandardError.ReadToEndAsync()
    if (!$verifier.WaitForExit(120000)) { $verifier.Kill(); throw 'Exact release verifier exceeded its deadline.' }
    $stdout.GetAwaiter().GetResult() | Write-Host
    if ($verifier.ExitCode -ne 0) { $stderr.GetAwaiter().GetResult() | Write-Host; throw 'Release signature/archive/inventory verification failed. Nothing published.' }
} finally { $verifier.Dispose() }
# Explicit publication command. A matching pushed tag is required; create a draft for final review.
& gh release create "v$($manifest.version)" $archive $manifestFile $signatureFile --repo 'bingbongs/VRC-SWITCHEROONIE' --verify-tag --draft --title "VRC-SWITCHEROONIE $($manifest.version)" --notes-file ([IO.Path]::GetFullPath($NotesFile))
if ($LASTEXITCODE -ne 0) { throw 'GitHub release creation failed.' }
