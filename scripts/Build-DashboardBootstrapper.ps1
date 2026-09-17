[CmdletBinding()]
param([string] $Version = '1.0.19')
. (Join-Path $PSScriptRoot 'PrerequisiteSecurity.ps1')
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'installers\AgentSignaler.Dashboard.Bundle'
$output = Join-Path $root 'artifacts\bootstrapper'
$tools = Join-Path $root 'artifacts\installer-tools'
New-Item -ItemType Directory -Path $output, $tools -Force | Out-Null
$pins = @{
    'BootstrapperApplication.h' = '66D7915006D5FF02EC00812F3E7856061C57229FED73BF32CD44E752CE57FF37'
    'BootstrapperEngine.h' = '0D95B0D1B4DBE655BC8949C299238CB1540F65CE092E2510C22BBEE0F9301EEB'
}
foreach ($entry in $pins.GetEnumerator()) {
    $path = Join-Path $tools $entry.Key
    if (-not (Test-Path -LiteralPath $path)) {
        $url = "https://raw.githubusercontent.com/wixtoolset/wix/v4.0.6/src/api/burn/WixToolset.BootstrapperCore.Native/inc/$($entry.Key)"
        & curl.exe --fail --silent --show-error --proto '=https' --max-time 120 --output $path $url
        if ($LASTEXITCODE) { throw 'Pinned WiX 4.0.6 native API header download failed.' }
    }
    if ((Get-FileHash -LiteralPath $path).Hash -cne $entry.Value) { throw 'WiX 4.0.6 native API header hash mismatch.' }
}
Get-VerifiedPrerequisitePayloads
$metadata = Get-PrerequisiteMetadata
$metadata['DashboardVersion'] = $Version
$header = "#pragma once`r`n"
foreach ($entry in $metadata.GetEnumerator()) {
    if ($entry.Key -notmatch '^[A-Za-z][A-Za-z0-9]*$' -or $entry.Value -match '["\\\r\n]') { throw 'Unsafe generated prerequisite constant.' }
    $header += "inline constexpr wchar_t $($entry.Key)[] = L`"$($entry.Value)`";`r`n"
}
[IO.File]::WriteAllText((Join-Path $output 'Prerequisites.generated.h'), $header)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'An existing Visual Studio x64 C++ toolchain is required; no toolchain will be installed.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$env:PATH = (Split-Path -Parent $vswhere) + ';' + $env:PATH
$common = "/nologo /std:c++17 /EHsc /W4 /WX /MT /O2 /guard:cf /DUNICODE /D_UNICODE /I`"$tools`" /I`"$output`" /I`"$source`""
$build = "call `"$vcvars`" >nul && cl $common /LD `"$source\Bootstrapper.cpp`" /Fo`"$output\Bootstrapper.obj`" /Fe`"$output\AgentSignaler.Bootstrapper.dll`" /link /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /guard:cf msi.lib wintrust.lib crypt32.lib bcrypt.lib shell32.lib ole32.lib user32.lib gdi32.lib comctl32.lib advapi32.lib version.lib"
& $env:ComSpec /d /c $build
if ($LASTEXITCODE) { throw 'Native bootstrapper build failed.' }
$testBuild = "call `"$vcvars`" >nul && cl $common `"$source\BootstrapperTests.cpp`" /Fo`"$output\BootstrapperTests.obj`" /Fe`"$output\BootstrapperTests.exe`" /link msi.lib wintrust.lib crypt32.lib bcrypt.lib shell32.lib ole32.lib user32.lib gdi32.lib comctl32.lib advapi32.lib version.lib"
& $env:ComSpec /d /c $testBuild
if ($LASTEXITCODE) { throw 'Native bootstrapper policy test build failed.' }
& (Join-Path $output 'BootstrapperTests.exe')
if ($LASTEXITCODE) { throw 'Native bootstrapper policy tests failed.' }
Assert-X64Executable (Join-Path $output 'AgentSignaler.Bootstrapper.dll')
