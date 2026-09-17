[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $Path)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$installer = New-Object -ComObject WindowsInstaller.Installer
try {
    $database = $installer.OpenDatabase([IO.Path]::GetFullPath($Path), 0)
    try {
        $view = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''UpgradeCode''')
        try {
            [void]$view.Execute()
            $record = $view.Fetch()
            try {
                if ($null -eq $record -or $record.StringData(1) -cne '{A8D1F2A9-762B-4B2C-A5A3-451463D743B2}') {
                    throw 'Elevation authoring is restricted to the dedicated RpcHost package.'
                }
            }
            finally { if ($null -ne $record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) } }
        }
        finally { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
    $summary = $installer.SummaryInformation([IO.Path]::GetFullPath($Path), 1)
    try {
        $summary.Property(15) = ([int]$summary.Property(15) -band (-bnot 8))
        $summary.Persist()
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) }
}
finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
