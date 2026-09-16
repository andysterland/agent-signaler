[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$tracked = @()
if (Test-Path -LiteralPath (Join-Path $root '.git')) {
    $tracked = @(& git -C $root ls-files)
    if ($LASTEXITCODE) { throw 'Unable to enumerate tracked repository files.' }
}
$files = if ($tracked.Count -gt 0) {
    $tracked
}
else {
    @(
        Get-ChildItem -LiteralPath $root -File -Recurse -Force |
            Where-Object {
                $_.FullName -notmatch '[\\/](?:\.git|\.vs|artifacts|bin|obj|TestResults)[\\/]' -and
                $_.Name -notlike 'UpgradeLog*.htm' -and
                $_.FullName -ne (Join-Path $root 'docs\public-github-repository-migration-plan.md')
            } |
            ForEach-Object { $_.FullName.Substring($root.Length + 1) }
    )
}

$forbiddenExtensions = @('.msi', '.msix', '.exe', '.pfx', '.p12', '.snk', '.cer',
    '.key', '.pem', '.dmp', '.log', '.db', '.sqlite', '.zip')
$invalidFiles = @($files | Where-Object {
    $extension = [IO.Path]::GetExtension($_)
    $forbiddenExtensions -contains $extension.ToLowerInvariant()
})
if ($invalidFiles.Count -gt 0) {
    throw "Generated, secret-bearing, or release binary files are not allowed: $($invalidFiles -join ', ')"
}

$textExtensions = @('.cs', '.csproj', '.props', '.targets', '.slnx', '.wixproj',
    '.wxs', '.cpp', '.h', '.xaml', '.xml', '.xslt', '.ps1', '.cmd', '.bat',
    '.md', '.yml', '.yaml', '.json', '.http', '.manifest', '.svg', '.gitignore',
    '.gitattributes')
$privatePatterns = @(
    '(?i)\\\\' + 'tsclient\\',
    '(?i)Q:' + '\\src\\agent-signaler',
    '(?i)C:\\Users\\' + 'andster(?:\\|$)'
)
$secretPatterns = @(
    '(?i)-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '(?i)(?:client[_-]?secret|api[_-]?key|accountkey|sharedaccesssignature)\s*[:=]\s*["''][^"'']{8,}',
    '(?i)https://[^/\s:@]+:[^@\s/]+@'
)

$failures = [Collections.Generic.List[string]]::new()
function Test-AllowedSyntheticSecret([string] $Relative, [string] $Line) {
    $normalized = $Relative.Replace('/', '\')
    if (-not $normalized.StartsWith('tests\', [StringComparison]::OrdinalIgnoreCase) -or
        $Line.IndexOf('[InlineData(', [StringComparison]::Ordinal) -lt 0) {
        return $false
    }
    return $Line -match '(?i)(?:example\.test|desktop-pc|\.devtunnels\.ms)'
}

foreach ($relative in $files) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
    if ($relative -in @('.gitignore', '.gitattributes') -or $textExtensions -contains $extension) {
        $text = [IO.File]::ReadAllText($path)
        foreach ($pattern in $privatePatterns) {
            if ($text -match $pattern) { $failures.Add("$relative contains a private workstation or share path."); break }
        }
        foreach ($line in [IO.File]::ReadLines($path)) {
            foreach ($pattern in $secretPatterns) {
                if ($line -match $pattern -and -not (Test-AllowedSyntheticSecret $relative $line)) {
                    $failures.Add("$relative contains secret-shaped content.")
                    break
                }
            }
            if ($failures.Count -gt 0 -and $failures[$failures.Count - 1].StartsWith($relative, [StringComparison]::OrdinalIgnoreCase)) {
                break
            }
        }
    }
}

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}

Write-Host "$($files.Count) repository files passed public-path, binary, and secret-pattern checks."
