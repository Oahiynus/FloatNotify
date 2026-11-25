# Invoked by the installer with a private, extracted payload and inventory.
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Requirements','Install','Remove')][string]$Action,
      [string]$Destination,[string]$RegisteredDirectory,[string]$HookDirectory,[switch]$Connect)
$ErrorActionPreference='Stop'

function File-Digest([string]$path) {
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try{
        $stream=[IO.File]::OpenRead($path)
        try{return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','')}
        finally{$stream.Dispose()}
    }finally{$algorithm.Dispose()}
}

function Check-Requirements {
    $roots=@($env:DOTNET_ROOT_X64,$env:DOTNET_ROOT,(Join-Path $env:ProgramW6432 'dotnet'))
    foreach($view in 'Registry32','Registry64'){
        $lm=[Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine',$view)
        try{$k=$lm.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
            if($k){try{$roots+=$k.GetValue('InstallLocation')}finally{$k.Dispose()}}
        }finally{$lm.Dispose()}
    }
    $desktop=$false
    foreach($root in $roots|Where-Object {$_}|Select-Object -Unique){
        $fx=Join-Path $root 'shared\Microsoft.WindowsDesktop.App'
        if(Test-Path -LiteralPath $fx){
            foreach($dir in Get-ChildItem -LiteralPath $fx -Directory){
                if($dir.Name -match '^10\.0\.\d+$' -and (Test-Path -LiteralPath (Join-Path $dir.FullName 'PresentationFramework.dll'))){$desktop=$true;break}
            }
        }
        if($desktop){break}
    }
    if(!$desktop){exit 10}
    $client='SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    foreach($hive in 'CurrentUser','LocalMachine'){
        foreach($view in 'Registry32','Registry64'){
            $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey($hive,$view)
            try{
                $key=$base.OpenSubKey($client)
                if($key){try{$version=$key.GetValue('pv');if($version -and ([version]$version -gt [version]'0.0.0.0')){return}}finally{$key.Dispose()}}
            }finally{$base.Dispose()}
        }
    }
    exit 11
}

function Stop-InstalledResident([string]$exe) {
    $active=@(Get-Process -Name FloatNotify -ErrorAction SilentlyContinue|Where-Object {$_.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId -and $_.Path -eq $exe})
    if(!$active.Count){return}
    # Only this executable's resident may be stopped. An independent portable copy can be active.
    $helper=Start-Process -FilePath $exe -ArgumentList '--exit' -WindowStyle Hidden -PassThru
    try{
        if(!$helper.WaitForExit(5000)){throw 'Please exit FloatNotify from the tray and retry.'}
        foreach($p in $active){if(!$p.WaitForExit(5000)){throw 'Please exit FloatNotify from the tray and retry.'}}
    }finally{$helper.Dispose();foreach($p in $active){$p.Dispose()}}
}

try {
    if($Action -eq 'Requirements'){Check-Requirements;exit 0}
    $dest=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if($dest.Length -le [IO.Path]::GetPathRoot($dest).Length){throw 'Choose a dedicated application directory.'}
    $owned=$RegisteredDirectory -and ([IO.Path]::GetFullPath($RegisteredDirectory).TrimEnd('\') -eq $dest)
    $present=[IO.Directory]::Exists($dest)
    if($RegisteredDirectory -and !$owned){throw 'Use the registered directory, or uninstall before moving the installation.'}
    if($Action -eq 'Remove' -and !$owned){throw 'This directory does not match the registered installation.'}
    if($Action -eq 'Install' -and !$owned -and $present -and @(Get-ChildItem -LiteralPath $dest -Force).Count){throw 'Choose a new or empty application directory.'}
    $exe=Join-Path $dest 'FloatNotify.exe'
    $files=[ordered]@{}
    foreach($line in [IO.File]::ReadAllLines((Join-Path $PSScriptRoot 'inventory.txt'))){
        if($line -notmatch '^([0-9A-F]{64})  ([A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*)$'){throw 'Invalid package inventory.'}
        $rel=$Matches[2];$hash=$Matches[1]
        if($rel.Split('/') -contains '..' -or $files.Contains($rel)){throw 'Invalid package path.'}
        $files.Add($rel,$hash)
    }
    $names=@($files.Keys)+@('Uninstall.exe')
    foreach($name in $names){
        $candidate=[IO.Path]::GetFullPath((Join-Path $dest $name))
        if(!$candidate.StartsWith($dest+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid target path.'}
        for($p=$candidate;$p -and $p.Length -ge $dest.Length;$p=[IO.Path]::GetDirectoryName($p)){
            if((Test-Path -LiteralPath $p) -and ((Get-Item -Force -LiteralPath $p).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'An installation path points to a link.'}
        }
    }
    if($Action -eq 'Remove'){
        Stop-InstalledResident $exe
        # Use the clean registration script in the uninstaller even if the app copy was removed.
        & (Join-Path $PSScriptRoot 'Register.ps1') -Remove -ConfigDirectory $HookDirectory -ApplicationPath $exe
        $run=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
        if($run){try{if($run.GetValue('FloatNotify') -eq ('"'+$exe+'" --background')){$run.DeleteValue('FloatNotify',$false)}}finally{$run.Dispose()}}
        foreach($rel in $files.Keys){$path=Join-Path $dest $rel;if([IO.File]::Exists($path)){[IO.File]::Delete($path)}}
        # The uninstaller removes its own image and empty directories after this process exits.
        exit 0
    }
    $payload=Join-Path $PSScriptRoot 'payload'
    $rollback=Join-Path $PSScriptRoot 'rollback'
    foreach($rel in $files.Keys){
        if((File-Digest (Join-Path $payload $rel)) -ne $files[$rel]){throw "Damaged package file: $rel"}
    }
    if($owned){Stop-InstalledResident $exe}
    $copied=New-Object 'System.Collections.Generic.List[string]'
    try{
        foreach($rel in $names){
            $to=Join-Path $dest $rel
            if([IO.File]::Exists($to)){
                $old=Join-Path $rollback $rel
                $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($old))
                [IO.File]::Copy($to,$old)
            }
            $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
            $from=if($rel -eq 'Uninstall.exe'){Join-Path $PSScriptRoot 'Uninstall.exe'}else{Join-Path $payload $rel}
            $copied.Add($rel)
            [IO.File]::Copy($from,$to,$true)
            if($files.Contains($rel) -and (File-Digest $to) -ne $files[$rel]){throw "Copy failed: $rel"}
        }
        if($Connect){& (Join-Path $dest 'Register.ps1') -ConfigDirectory $HookDirectory -IfAvailable}
    }catch{
        $original=$_
        foreach($rel in $copied){
            $old=Join-Path $rollback $rel
            $to=Join-Path $dest $rel
            if([IO.File]::Exists($old)){[IO.File]::Copy($old,$to,$true)}else{[IO.File]::Delete($to)}
        }
        foreach($dir in @('SettingsUI','Notices','.')){
            $path=Join-Path $dest $dir
            if([IO.Directory]::Exists($path) -and ![IO.Directory]::GetFileSystemEntries($path).Length){[IO.Directory]::Delete([IO.Path]::GetFullPath($path))}
        }
        throw $original
    }
}catch{[Console]::Error.WriteLine($_.Exception.Message);exit 1}

