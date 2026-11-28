#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageFamilyName)
$ErrorActionPreference = 'Stop'
# Let Windows initialize the package's data store, including its ACLs and any
# redirection to another app volume, before the harness writes fixture settings.
$manager = [Windows.Management.Core.ApplicationDataManager, Windows.Management.Core, ContentType=WindowsRuntime]
$manager::CreateForPackageFamily($PackageFamilyName).LocalFolder.Path
