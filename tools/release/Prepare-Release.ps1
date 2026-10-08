param(
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$Output,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][long]$Sequence
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})\.(0|[1-9][0-9]{0,5})$' -or $Sequence -lt 1) { throw 'Invalid release version or sequence.' }
$packagePath = [IO.Path]::GetFullPath($Package)
$outputPath = [IO.Path]::GetFullPath($Output)
$helper = Join-Path $packagePath 'Switcheroonie.Updater.exe'
if (!(Test-Path -LiteralPath $helper -PathType Leaf)) { throw 'Build the complete portable package first.' }
# Windows command-line quoting. No shell evaluates these arguments.
function Quote([string]$Value) {
    if ($Value.Contains('"') -or $Value.Contains([char]0)) { throw 'Ambiguous release argument.' }
    return '"' + $Value.TrimEnd('\') + '"'
}
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $helper
$start.Arguments = '--prepare-release ' + (Quote $packagePath) + ' ' + (Quote $outputPath) + ' ' + (Quote $Version) + ' ' + $Sequence
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
try {
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(120000)) { $process.Kill(); throw 'Exact owned release helper exceeded its preparation deadline.' }
    $stdout.GetAwaiter().GetResult() | Write-Host
    if ($process.ExitCode -ne 0) { $stderr.GetAwaiter().GetResult() | Write-Host; throw 'Release signature preparation failed. Nothing published.' }
} finally { $process.Dispose() }
if (Test-Path -LiteralPath (Join-Path $packagePath 'portable\VRC-SWITCHEROONIE.exe') -PathType Leaf) {
    & (Join-Path $PSScriptRoot '..\New-StandaloneArchive.ps1') -Package $packagePath -Output (Join-Path $outputPath 'VRC-SWITCHEROONIE-Portable.zip')
}
