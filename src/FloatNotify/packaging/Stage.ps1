# PowerShell 7, x64 .NET SDK and the project's C++ build tools are required on the build host.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildDirectory,[string]$PackageSource)
$ErrorActionPreference='Stop'
$src=(Get-Item -LiteralPath (Join-Path $PSScriptRoot '..')).FullName
$build=[IO.Path]::GetFullPath($BuildDirectory)
if(Test-Path -LiteralPath $build){throw 'Choose a new, empty build path.'}
$clean=Join-Path $build 'source'
$publish=Join-Path $build 'publish'
$stage=Join-Path $build 'payload'
foreach($dir in $clean,$stage){$null=New-Item -ItemType Directory -Path $dir -Force}
$manifest=@(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'files.txt') | Where-Object {$_})
$sourceFiles=@('FloatNotify.csproj','NuGet.Config','BuildGlass.cmd','GlassBackdrop.cpp','GlassCapture.h','LiquidLens.cpp','Register.ps1',
    'Card.xaml','Card.xaml.cs','OverflowWindow.xaml','OverflowWindow.xaml.cs','CardPlacement.cs','Checks.cs','CodexHookReport.cs',
    'DesktopChatRoutes.cs','DiagnosticReport.cs','DirectChoices.cs','Events.cs','ForegroundChat.cs','FormBridge.cs','GlassSurface.cs',
    'LiquidLens.cs','LocalData.cs','Monitor.cs','OfficialApproval.cs','Program.cs','Resident.cs','Resident.Deck.cs',
    'Resident.Settings.cs','SettingsWindow.cs','ThreadWorkspaces.cs')
foreach($rel in @($sourceFiles)+@($manifest.Where({$_.StartsWith('SettingsUI/')}))){
    $dest=Join-Path $clean $rel
    $null=New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest)
    Copy-Item -LiteralPath (Join-Path $src $rel) -Destination $dest
}
$params=@('publish',(Join-Path $clean 'FloatNotify.csproj'),'-c','Release','-r','win-x64','--no-self-contained','-o',$publish,
    '-p:PlatformTarget=x64','-p:DebugType=None','-p:DebugSymbols=false','-p:IncludeSourceRevisionInInformationalVersion=false',
    "-p:PathMap=$clean=/_/FloatNotify",'-p:NuGetAudit=false')
if($PackageSource){$params+=@('--source',$PackageSource)}
& dotnet @params
if($LASTEXITCODE){throw 'Release build failed.'}
[xml]$project=Get-Content -Raw -LiteralPath (Join-Path $clean 'FloatNotify.csproj')
$sdkVersion=($project.Project.ItemGroup.PackageReference | Where-Object Include -eq 'Microsoft.Web.WebView2').Version
$assets=Get-Content -Raw -LiteralPath (Join-Path $clean 'obj/project.assets.json')|ConvertFrom-Json -AsHashtable
$pkg=@($assets.packageFolders.Keys|ForEach-Object {Join-Path $_ "microsoft.web.webview2/$sdkVersion"}|Where-Object {Test-Path -LiteralPath (Join-Path $_ 'LICENSE.txt')})[0]
foreach($rel in $manifest){
    $from=switch($rel){
        'Notices/WebView2-LICENSE.txt' {Join-Path $pkg 'LICENSE.txt'}
        'Notices/WebView2-NOTICE.txt' {Join-Path $pkg 'NOTICE.txt'}
        default {Join-Path $publish $rel}
    }
    $to=Join-Path $stage $rel
    $null=New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to)
    if($rel -eq 'Register.ps1'){
        # Windows PowerShell 5.1 needs a BOM to read the localized status text.
        [IO.File]::WriteAllText($to,[IO.File]::ReadAllText($from),[Text.UTF8Encoding]::new($true))
    }else{Copy-Item -LiteralPath $from -Destination $to}
}
$actual=@(Get-ChildItem -LiteralPath $stage -Recurse -File|ForEach-Object {[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')})
if(Compare-Object ($manifest|Sort-Object) ($actual|Sort-Object)){throw 'Unexpected payload inventory.'}
$version=[string]$project.Project.PropertyGroup.Version
if((Get-Item -LiteralPath (Join-Path $stage 'FloatNotify.exe')).VersionInfo.ProductVersion -ne $version){throw 'Application version mismatch.'}
& dotnet (Join-Path $stage 'FloatNotify.dll') --self-test
if($LASTEXITCODE){throw 'Packaged application self-test failed.'}
Write-Output "$version / win-x64 / $($actual.Count) application files staged."
