$ErrorActionPreference = "Stop"

$managed = Join-Path $env:GITHUB_WORKSPACE "server\7DaysToDieServer_Data\Managed"
if (!(Test-Path $managed)) { throw "No se encontró Managed: $managed" }

$harmony = Get-ChildItem (Join-Path $env:GITHUB_WORKSPACE "packages") -Recurse -Filter 0Harmony.dll |
    Where-Object { $_.FullName -match 'Lib\.Harmony' } |
    Select-Object -First 1 -ExpandProperty FullName
if (!(Test-Path $harmony)) { throw "No se encontró 0Harmony.dll" }

$csc = Get-ChildItem (Join-Path $env:GITHUB_WORKSPACE "packages") -Recurse -Filter csc.exe |
    Where-Object { $_.FullName -match 'Microsoft\.Net\.Compilers\.Toolset' -and $_.FullName -match 'net472' } |
    Select-Object -First 1 -ExpandProperty FullName
if (!(Test-Path $csc)) {
    $csc = Get-ChildItem (Join-Path $env:GITHUB_WORKSPACE "packages") -Recurse -Filter csc.exe |
        Where-Object { $_.FullName -match 'Microsoft\.Net\.Compilers\.Toolset' } |
        Select-Object -First 1 -ExpandProperty FullName
}
if (!(Test-Path $csc)) { throw "No se encontró Roslyn csc.exe" }

$rsp = Join-Path $env:GITHUB_WORKSPACE "refs.rsp"
$lines = @(
    '/nologo',
    '/target:library',
    '/optimize+',
    '/langversion:latest',
    '/nostdlib+',
    '/out:out\bsg_BestiaryChronicle.dll'
)

# Referenciar exactamente el runtime que trae 7DTD/Unity evita mezclar el
# Framework clásico del runner con netstandard 2.1 del juego.
Get-ChildItem $managed -Filter *.dll | Sort-Object Name | ForEach-Object {
    $lines += ('/reference:"' + $_.FullName + '"')
}
$lines += ('/reference:"' + $harmony + '"')
$lines += ('"' + (Join-Path $env:GITHUB_WORKSPACE 'src\BsgChronicleV07.cs') + '"')
$lines | Set-Content -Path $rsp -Encoding UTF8

New-Item -ItemType Directory -Force -Path (Join-Path $env:GITHUB_WORKSPACE 'out') | Out-Null
Write-Host "Compiler: $csc"
& $csc "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "csc devolvió $LASTEXITCODE" }

Get-Item (Join-Path $env:GITHUB_WORKSPACE 'out\bsg_BestiaryChronicle.dll') | Format-List FullName,Length
