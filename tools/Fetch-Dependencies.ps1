param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$commit = '0924064316de3effbcd1acf1e309182a2deb1c05'
$dependencies = @(
    @{path='bin/win64/openvr_api.dll'; hash='BAB8AC6EF64E68A9CA53315B0014D131088584B2EFDFA6DB511D67EC03CFCB4A'},
    @{path='lib/win64/openvr_api.lib'; hash='A0BF57C5920F569E8D21AB3E5BC95BAC4B73E2016217F8B5B93495A2A7197BBB'}
)
foreach ($item in $dependencies) {
    $target = Join-Path (Join-Path $repo 'third_party/openvr') $item.path
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $item.hash) { throw 'Existing OpenVR binary has an unexpected hash; preserved for review.' }
        continue
    }
    $parent = Split-Path -Parent $target
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $temporary = Join-Path $parent ('.fetch-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/ValveSoftware/openvr/$commit/$($item.path)" -OutFile $temporary -TimeoutSec 60
        if ((Get-Item -LiteralPath $temporary).Length -gt 8MB -or (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $item.hash) { throw 'Pinned OpenVR download failed hash/size validation.' }
        [IO.File]::Move($temporary, $target)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
Write-Host 'Pinned official OpenVR dependency hashes verified.'
