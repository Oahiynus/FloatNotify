# Windows PowerShell 5.1 and PowerShell 7. Registers this installation's Stop and interactive PermissionRequest hooks.
[CmdletBinding()]
param([switch]$Remove, [string]$ConfigDirectory, [switch]$IfAvailable, [switch]$ShowResult, [string]$ApplicationPath)
$ErrorActionPreference='Stop'
function Show-Result([string]$message){
    if($ShowResult){Add-Type -AssemblyName System.Windows.Forms
        $null=[Windows.Forms.MessageBox]::Show($message,'浮笺')}
}
trap {Show-Result $_.Exception.Message;throw $_}
$legacy=$PSVersionTable.PSVersion.Major -lt 6
if($legacy){
    Add-Type -AssemblyName System.Web.Extensions
    $serializer=New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $serializer.MaxJsonLength=[int]::MaxValue
    $serializer.RecursionLimit=100
}
$binary=Join-Path $PSScriptRoot 'FloatNotify.exe'
if($ApplicationPath){
    if(!$Remove){throw 'An explicit executable path is only supported for removal.'}
    $binary=[IO.Path]::GetFullPath($ApplicationPath)
}
if (-not $Remove -and -not (Test-Path -LiteralPath $binary -PathType Leaf)) {throw 'Place this script beside FloatNotify.exe.'}
$codexFolder=if ($ConfigDirectory) {$ConfigDirectory} elseif ($env:CODEX_HOME) {$env:CODEX_HOME} else {Join-Path $env:USERPROFILE '.codex'}
if (-not (Test-Path -LiteralPath $codexFolder -PathType Container)){
    if($IfAvailable -or $Remove){return};throw '请先启动一次 Codex，再重新接入。'
}
$configPath=Join-Path $codexFolder 'hooks.json'
$quoted=$binary.Replace("'","''")
$command="& '$quoted' --hook"
$encoding=[Text.UTF8Encoding]::new($false)
$lock=[Threading.Mutex]::new($false,'Local\FloatNotify.HookRegistration')
try {
    if (-not $lock.WaitOne(3000)){throw 'Another hook configuration edit is running.'}
    $existed=[IO.File]::Exists($configPath)
    $before=if ($existed){[IO.File]::ReadAllText($configPath)}else{''}
    $cfg=@{}
    if($existed){
        if($legacy){$cfg=$serializer.DeserializeObject($before)}
        else{$cfg=ConvertFrom-Json -InputObject $before -AsHashtable}
    }
    if($cfg -isnot [Collections.IDictionary]){throw 'Invalid hooks.json root; original preserved.'}
    if(-not $cfg.ContainsKey('hooks')){if($Remove){return};$cfg['hooks']=@{}}
    if($cfg['hooks'] -isnot [Collections.IDictionary]){throw 'Invalid hooks container; original preserved.'}
    # Two independent installations answering the same permission hook would race.
    if(!$Remove){
        foreach($eventName in 'Stop','PermissionRequest'){
            foreach($group in $cfg['hooks'][$eventName]){
                foreach($item in $group.hooks){
                    $existing=[string]$item.commandWindows
                    if($existing -and $item.command -ieq $existing -and $existing -ine $command -and
                       $existing -match "^& '.+[\\/]FloatNotify\.exe' --hook$"){
                        if($IfAvailable){Write-Output 'FLOATNOTIFY_OTHER_INSTALLATION';return}
                        throw 'Codex 已接入另一安装位置的浮笺。要切换位置，请先使用原安装的卸载或移除 Hook 功能。'
                    }
                }
            }
        }
    }
    $changed=$false
    foreach($eventName in 'Stop','PermissionRequest'){
        $groups=@($cfg['hooks'][$eventName] | Where-Object {$null -ne $_})
        $matched=$false
        $list=[Collections.Generic.List[object]]::new()
        foreach($group in $groups){
            $kept=[Collections.Generic.List[object]]::new()
            $removed=$false
            foreach($entry in $group.hooks){
                if($entry.command -ieq $command -and $entry.commandWindows -ieq $command){
                    $matched=$true
                    if($Remove){$removed=$true;$changed=$true;continue}
                }
                $kept.Add($entry)
            }
            if(-not $removed -or $kept.Count){
                if($removed){$group['hooks']=$kept.ToArray()}
                $list.Add($group)
            }
        }
        if(-not $Remove -and -not $matched){
            $entry=@{type='command';command=$command;commandWindows=$command}
            if($eventName -eq 'Stop'){$entry.async=$true;$entry.timeout=5;$entry.statusMessage='浮笺提醒'}
            else{$entry.timeout=120;$entry.statusMessage='等待浮笺中的人工审批'}
            $list.Add(@{hooks=@($entry)});$changed=$true
        }
        if($Remove -and -not $matched){continue}
        if($list.Count){$cfg['hooks'][$eventName]=$list.ToArray()}else{$null=$cfg['hooks'].Remove($eventName)}
    }
    if(-not $changed){Show-Result '浮笺的 Hook 已配置。请在 Codex 设置中核对并信任浮笺的两个 Hook。';Write-Output 'Already in the requested state.';return}
    $json=if($legacy){$serializer.Serialize($cfg)}else{ConvertTo-Json -InputObject $cfg -Depth 100}
    $newText=$json+[Environment]::NewLine
    $temp=Join-Path $codexFolder ('.floatnotify-'+[guid]::NewGuid().ToString('N')+'.tmp')
    try {
        [IO.File]::WriteAllText($temp,$newText,$encoding)
        if($existed){
            if ([IO.File]::ReadAllText($configPath) -cne $before){throw 'hooks.json changed concurrently. Retry.'}
            $backup=Join-Path $codexFolder ('hooks.before-floatnotify-'+[guid]::NewGuid().ToString('N')+'.json')
            [IO.File]::Replace($temp,$configPath,$backup)
            Write-Output ('Backup: '+$backup)
        }else{[IO.File]::Move($temp,$configPath)}
    }finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
    Show-Result '接入已完成。请在 Codex 设置中核对并信任浮笺的两个 Hook。'
    Write-Output ('Updated: '+$configPath)
}finally{try{$lock.ReleaseMutex()}catch [ApplicationException]{};$lock.Dispose()}
