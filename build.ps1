$ErrorActionPreference = "Stop"

$managed = Join-Path $env:GITHUB_WORKSPACE "server\7DaysToDieServer_Data\Managed"
if (!(Test-Path $managed)) { throw "No se encontró Managed: $managed" }

$harmony = Join-Path $env:GITHUB_WORKSPACE "packages\Lib.Harmony.2.3.3\lib\net472\0Harmony.dll"
if (!(Test-Path $harmony)) {
    $harmony = Get-ChildItem (Join-Path $env:GITHUB_WORKSPACE "packages") -Recurse -Filter 0Harmony.dll | Select-Object -First 1 -ExpandProperty FullName
}
if (!(Test-Path $harmony)) { throw "No se encontró 0Harmony.dll" }

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (!(Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (!(Test-Path $csc)) { throw "No se encontró csc.exe" }

$exclude = @(
    'mscorlib.dll','Accessibility.dll','Microsoft.CSharp.dll','netstandard.dll','System.dll','System.Core.dll',
    'System.Xml.dll','System.Xml.Linq.dll','System.Data.dll','System.Drawing.dll','System.Configuration.dll',
    'System.Numerics.dll','System.Runtime.Serialization.dll','System.Net.Http.dll','System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll','System.ValueTuple.dll'
)

$refs = Get-ChildItem $managed -Filter *.dll | Where-Object { $exclude -notcontains $_.Name }
$rsp = Join-Path $env:GITHUB_WORKSPACE "refs.rsp"
$lines = @(
    '/nologo',
    '/target:library',
    '/optimize+',
    '/langversion:latest',
    '/out:out\bsg_BestiaryChronicle.dll',
    '/reference:System.Xml.Linq.dll',
    ('/reference:"' + $harmony + '"')
)
foreach ($r in $refs) { $lines += ('/reference:"' + $r.FullName + '"') }
$lines += ('"' + (Join-Path $env:GITHUB_WORKSPACE 'src\BsgChronicleV07.cs') + '"')
$lines | Set-Content -Path $rsp -Encoding ASCII

New-Item -ItemType Directory -Force -Path (Join-Path $env:GITHUB_WORKSPACE 'out') | Out-Null
& $csc "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "csc devolvió $LASTEXITCODE" }

Get-Item (Join-Path $env:GITHUB_WORKSPACE 'out\bsg_BestiaryChronicle.dll') | Format-List FullName,Length
