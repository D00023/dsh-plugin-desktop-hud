# Build HUD.exe with the in-box .NET Framework 4.x compiler.
# No MSBuild / SDK / BAML: plain C# + an embedded XAML layout, GAC references only.
#
# Usage (from anywhere):
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Out bin-dev\HUD.exe
#
# Output: bin\HUD.exe (or the -Out path)

param(
    [string]$Out = 'bin\HUD.exe'
)

$ErrorActionPreference = 'Stop'

# The project directory contains non-ASCII characters; keep every native-compiler
# argument relative and run csc with this directory as its working directory.
Set-Location -LiteralPath $PSScriptRoot

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "Compiler not found: $csc" }

# /noconfig is required: the default csc.rsp already references System.Drawing /
# System.Windows.Forms from the framework directory, which collides (CS1703) with the
# explicit GAC references below. Everything we need is listed here explicitly.
$refs = @(
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System\v4.0_4.0.0.0__b77a5c561934e089\System.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Core\v4.0_4.0.0.0__b77a5c561934e089\System.Core.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xml\v4.0_4.0.0.0__b77a5c561934e089\System.Xml.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Drawing\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Drawing.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Windows.Forms\v4.0_4.0.0.0__b77a5c561934e089\System.Windows.Forms.dll'
)
foreach ($r in $refs) {
    if (-not (Test-Path -LiteralPath $r)) { throw "Missing GAC reference: $r" }
}

$sources = @(Get-ChildItem -LiteralPath 'src' -Filter '*.cs' | Sort-Object Name | ForEach-Object { 'src\' + $_.Name })
if ($sources.Count -eq 0) { throw 'No sources found in src\' }

$outFull = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Out))
$outDir = Split-Path -Parent $outFull
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$cscArgs = @(
    '/nologo',
    '/noconfig',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/codepage:65001',
    '/utf8output',
    '/win32manifest:app.manifest',
    '/resource:HudWindow.xaml,HudWindow.xaml',
    ('/out:' + $Out)
)
foreach ($r in $refs) { $cscArgs += ('/r:' + $r) }
$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc.exe failed with exit code $LASTEXITCODE" }

$exe = $outFull
if (-not (Test-Path -LiteralPath $exe)) { throw ('csc reported success but ' + $Out + ' is missing') }

$info = Get-Item -LiteralPath $exe
Write-Output ("BUILD OK -> {0} ({1} bytes, {2})" -f $info.FullName, $info.Length, $info.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))