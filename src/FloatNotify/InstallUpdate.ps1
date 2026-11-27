param(
    [Parameter(Mandatory)][string]$InstallDirectory,
    [Parameter(Mandatory)][version]$Version,
    [Parameter(Mandatory)][string]$Sha256
)
$ErrorActionPreference='Stop'
$name="FloatNotify-$Version-windows-x64-Setup.exe"
$setup=Join-Path $PSScriptRoot $name
$app=Join-Path $InstallDirectory 'FloatNotify.exe'
$started=$false
$ok=$false
$stream=$null
try {
    $entry=Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FloatNotify' -ErrorAction SilentlyContinue
    if($entry.InstallLocation -and [IO.Path]::GetFullPath($entry.InstallLocation).TrimEnd('\') -ne $InstallDirectory){
        throw '当前程序目录与已登记的浮笺安装目录不同。请在已登记的浮笺中更新。'
    }
    # Keep the verified image read-locked through setup launch and execution.
    $stream=[IO.File]::Open($setup,'Open','Read','Read')
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try{$actual=[BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','')}finally{$algorithm.Dispose()}
    if($actual -ne $Sha256){throw '安装包 SHA-256 校验失败。'}
    if([Diagnostics.FileVersionInfo]::GetVersionInfo($setup).ProductVersion -ne $Version.ToString()){throw '安装包版本与发布标签不一致。'}
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$setup
    # NSIS requires /D as the last argument, with an unquoted absolute path.
    $info.Arguments='/S /APPUPDATE /D='+$InstallDirectory
    $info.UseShellExecute=$false
    $process=[Diagnostics.Process]::Start($info)
    $started=$true
    try{$process.WaitForExit();$code=$process.ExitCode}finally{$process.Dispose()}
    if($code -ne 0){throw "安装器退出码为 $code。"}
    if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion -ne $Version.ToString()){throw '安装后版本核对失败。'}
    $ok=$true
} catch {
    $reason=$_.Exception.Message
} finally {
    if($stream){$stream.Dispose()}
}
if($started -and (Test-Path -LiteralPath $app)){
    try{
        $info=New-Object Diagnostics.ProcessStartInfo
        $info.FileName=$app
        $info.Arguments='--settings'
        $info.WorkingDirectory=$InstallDirectory
        $info.UseShellExecute=$false
        if($ok){$info.EnvironmentVariables['FLOATNOTIFY_UPDATED_TO']=$Version.ToString()}
        [Diagnostics.Process]::Start($info).Dispose()
    }catch{$ok=$false;$reason='无法重新打开浮笺。'+$_.Exception.Message}
}
if(!$ok){
    Add-Type -AssemblyName System.Windows.Forms
    [void][Windows.Forms.MessageBox]::Show($reason,'浮笺更新未完成')
}
foreach($path in @($setup,$PSCommandPath)){Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue}
# Delete only the now-empty directory allocated for this download.
try{[IO.Directory]::Delete($PSScriptRoot)}catch{}
if(!$ok){exit 1}
