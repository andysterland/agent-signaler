[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'installers\AgentSignaler.RpcHost'
$output = Join-Path $root 'artifacts\rpchost-installer'
[void](New-Item -ItemType Directory -Path $output -Force)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'The existing Visual Studio x64 C++ toolchain is required.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$env:PATH = (Split-Path -Parent $vswhere) + ';' + $env:PATH
$common = "/nologo /std:c++17 /EHsc /W4 /WX /MT /O2 /guard:cf /DUNICODE /D_UNICODE /I`"$source`""
$libraries = 'msi.lib ole32.lib oleaut32.lib advapi32.lib shell32.lib'
& $env:ComSpec /d /c "call `"$vcvars`" >nul && cl $common /LD `"$source\InstallerActions.cpp`" /Fo`"$output\InstallerActions.obj`" /Fe`"$output\AgentSignaler.RpcHost.InstallerActions.dll`" /link /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /guard:cf $libraries"
if ($LASTEXITCODE) { throw 'RpcHost installer custom-action build failed.' }
& $env:ComSpec /d /c "call `"$vcvars`" >nul && cl $common `"$source\InstallerPolicyTests.cpp`" /Fo`"$output\InstallerPolicyTests.obj`" /Fe`"$output\InstallerPolicyTests.exe`" /link $libraries"
if ($LASTEXITCODE) { throw 'RpcHost installer policy test build failed.' }
& (Join-Path $output 'InstallerPolicyTests.exe')
if ($LASTEXITCODE) { throw 'RpcHost installer policy tests failed.' }
& $env:ComSpec /d /c "call `"$vcvars`" >nul && cl $common `"$source\InstallerBoundaryTests.cpp`" /Fo`"$output\InstallerBoundaryTests.obj`" /Fe`"$output\InstallerBoundaryTests.exe`" /link $libraries"
if ($LASTEXITCODE) { throw 'RpcHost installer boundary test build failed.' }
$fixture = Join-Path $output ('fixture-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $fixture -Force)
try {
    & (Join-Path $output 'InstallerBoundaryTests.exe') $fixture
    if ($LASTEXITCODE) { throw 'RpcHost installer identity boundary tests failed.' }
}
finally { Remove-Item -LiteralPath $fixture -Recurse -Force }
