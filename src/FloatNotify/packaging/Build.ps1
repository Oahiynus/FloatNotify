# Build with PowerShell 7. Use the Unicode compiler from the NSIS 3 distribution.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Compiler,[Parameter(Mandatory)][string]$BuildDirectory,
      [Parameter(Mandatory)][string]$OutputDirectory,[string]$PackageSource)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Stage.ps1') -BuildDirectory $BuildDirectory -PackageSource $PackageSource
$build=(Get-Item -LiteralPath $BuildDirectory).FullName
$stage=Join-Path $build 'payload'
$version=(Get-Item -LiteralPath (Join-Path $stage 'FloatNotify.exe')).VersionInfo.ProductVersion
$output=[IO.Path]::GetFullPath($OutputDirectory)
$null=[IO.Directory]::CreateDirectory($output)
$installer=Join-Path $output "FloatNotify-$version-windows-x64-Setup.exe"
if(Test-Path -LiteralPath $installer){throw 'Use an empty output path to avoid replacing a release.'}
$manifest=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'files.txt') | Where-Object {$_})
$utf8=[Text.UTF8Encoding]::new($true)
$inventory=Join-Path $build 'inventory.txt'
$include=Join-Path $build 'payload.nsh'
$support=Join-Path $build 'InstallSupport.ps1'
$hashes=foreach($rel in $manifest){"$((Get-FileHash -LiteralPath (Join-Path $stage $rel)).Hash)  $rel"}
[IO.File]::WriteAllLines($inventory,[string[]]$hashes,$utf8)
$inputLines=foreach($rel in $manifest){
    $folder=[IO.Path]::GetDirectoryName($rel)
    $leaf=[IO.Path]::GetFileName($rel)
    if($folder){'SetOutPath "$PLUGINSDIR\payload\'+$folder+'"'}else{'SetOutPath "$PLUGINSDIR\payload"'}
    'File "/oname='+$leaf+'" "${Stage}\'+$rel.Replace('/','\')+'"'
}
[IO.File]::WriteAllLines($include,[string[]]$inputLines,$utf8)
[IO.File]::WriteAllText($support,[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'InstallSupport.ps1')),$utf8)
$argsList=@('/INPUTCHARSET','UTF8','/V3',"/DAppVersion=$version","/DStage=$stage","/DSupport=$support",
    "/DInventory=$inventory","/DPayloadInclude=$include","/DOutputFile=$installer",(Join-Path $PSScriptRoot 'FloatNotify.nsi'))
& $Compiler @argsList
if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $installer -PathType Leaf)){throw 'Installer compilation failed.'}
Get-FileHash -LiteralPath $installer

