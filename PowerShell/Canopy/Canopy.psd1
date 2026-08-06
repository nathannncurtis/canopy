@{
    RootModule = 'Canopy.psm1'
    ModuleVersion = '0.1.0'
    GUID = '8cda3f90-71d8-45d1-80a8-85d5cb769f4f'
    Author = 'Canopy contributors'
    CompanyName = 'Canopy'
    Copyright = '(c) Canopy contributors. Licensed under GPL-3.0.'
    Description = 'PowerShell commands for automating the Canopy disk scanner CLI.'
    PowerShellVersion = '7.2'
    FunctionsToExport = @('Invoke-CanopyScan')
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            Tags = @('Canopy', 'Disk', 'Storage', 'Scan')
            ProjectUri = 'https://github.com/nathannncurtis/canopy'
            LicenseUri = 'https://github.com/nathannncurtis/canopy/blob/main/LICENSE'
        }
    }
}
