param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][guid]$Chat,
    [guid]$ComparisonChat=[guid]::Empty,
    [switch]$TestInterruption
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CompletionTestWindow {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [StructLayout(LayoutKind.Sequential)] struct GuiThreadInfo {
        public int size, flags;
        public IntPtr active, focus, capture, menu, move, caret;
        public int x, y, right, bottom;
    }
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    public static IntPtr Focus(IntPtr window) {
        var info=new GuiThreadInfo{size=Marshal.SizeOf<GuiThreadInfo>()};
        return GetGUIThreadInfo(GetWindowThreadProcessId(window,IntPtr.Zero),ref info) ? info.focus : IntPtr.Zero;
    }
}
'@
$names=@{}
$index=Join-Path $env:USERPROFILE '.codex\session_index.jsonl'
Get-Content -Encoding UTF8 -LiteralPath $index | ForEach-Object {
    $row=$_ | ConvertFrom-Json
    $names[$row.id]=$row.thread_name
}
$condition=[System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Document)
function Wait-ForChat([bool]$Same, [guid]$Expected=[guid]::Empty) {
    $deadline=[DateTime]::UtcNow.AddSeconds(150)
    do {
        $h=[CompletionTestWindow]::GetForegroundWindow()
        if($h -ne [IntPtr]::Zero){
            try {
                $w=[System.Windows.Automation.AutomationElement]::FromHandle($h)
                $process=Get-Process -Id $w.Current.ProcessId
                if($process.ProcessName -in @('Codex','ChatGPT')){
                    $doc=$w.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
                    if($doc -and !$doc.Current.IsOffscreen){
                        $title=$doc.Current.Name
                        $ids=@($names.Keys | Where-Object {$names[$_] -ceq $title})
                        $matchesView=if($Same){$ids.Count -eq 1 -and [guid]$ids[0] -eq $Chat}
                            else{$ids.Count -gt 0 -and $ids -notcontains $Chat.ToString()}
                        if($matchesView -and ($Expected -eq [guid]::Empty -or $ids -contains $Expected.ToString()) -and
                            [CompletionTestWindow]::GetForegroundWindow() -eq $h){return $h}
                    }
                }
            } catch [System.Windows.Automation.ElementNotAvailableException] { }
              catch [System.ArgumentException] { }
        }
        Start-Sleep -Milliseconds 350
    } while([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for foreground conversation (same=$Same)."
}
$log=Join-Path $env:LOCALAPPDATA 'FloatNotify\events.log'
function Verify-Interruption([IO.StreamReader]$Reader,[string]$Turn){
    $original=[CompletionTestWindow]::GetForegroundWindow()
    $keyboard=[CompletionTestWindow]::Focus($original)
    $client=Start-Process -FilePath $Exe -ArgumentList '--preview','question' -WindowStyle Hidden -Wait -PassThru
    if($client.ExitCode -ne 0){throw 'Question preview was not delivered.'}
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    $byId=[System.Windows.Automation.AutomationElement]::AutomationIdProperty
    $descendants=[System.Windows.Automation.TreeScope]::Descendants
    $labelCondition=[System.Windows.Automation.PropertyCondition]::new($byId,'KindLabel')
    $ignoreCondition=[System.Windows.Automation.PropertyCondition]::new($byId,'Ignore')
    do {
        if([CompletionTestWindow]::GetForegroundWindow() -ne $original -or [CompletionTestWindow]::Focus($original) -ne $keyboard){throw 'Question preview changed the input focus.'}
        $hwnd=[CompletionTestWindow]::FindWindow([System.Management.Automation.Language.NullString]::Value,'浮笺提醒')
        if($hwnd -ne [IntPtr]::Zero) {
            $root=[System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            if((Get-Process -Id $root.Current.ProcessId).Path -eq $Exe){
                $label=$root.FindFirst($descendants,$labelCondition)
                if($label -and $label.Current.Name -eq 'ChatGPT丨需要你的回答'){
                    $ignore=$root.FindFirst($descendants,$ignoreCondition)
                    if($ignore){break}
                }
            }
        }
        Start-Sleep -Milliseconds 75
    } while([DateTime]::UtcNow -lt $deadline)
    if(!$ignore){throw 'Expected a visible question preview.'}
    $ignore.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    $text=''
    do {
        $text+=$Reader.ReadToEnd()
        if($text -match "done-shown source=preview focused=False centered=True .*turn=$Turn "){
            Write-Output 'PASS the question took priority and the original passive completion resumed.'
            return
        }
        Start-Sleep -Milliseconds 75
    } while([DateTime]::UtcNow -lt $deadline)
    throw 'The interrupted completion did not resume.'
}
function Verify-View([bool]$Same,[guid]$Expected=[guid]::Empty){
    $window=Wait-ForChat $Same $Expected
    $keyboard=[CompletionTestWindow]::Focus($window)
    if ($keyboard -eq [IntPtr]::Zero){throw "No focused control available in the foreground window."}
    $stream=[System.IO.File]::Open($log,'Open','Read',([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
    try {
        $null=$stream.Seek(0,'End')
        $reader=[System.IO.StreamReader]::new($stream)
        $client=Start-Process -FilePath $Exe -ArgumentList @('--preview','done',$Chat.ToString()) -WindowStyle Hidden -Wait -PassThru
        if($client.ExitCode -ne 0){throw 'Resident did not receive preview.'}
        $text=''
        $deadline=[DateTime]::UtcNow.AddSeconds(6)
        do {
            $text+=$reader.ReadToEnd()
            if([CompletionTestWindow]::GetForegroundWindow() -ne $window -or [CompletionTestWindow]::Focus($window) -ne $keyboard){throw "Foreground or keyboard focus changed before verdict."}
            if($text -match 'done-shown source=preview[^\r\n]*'){ $result=$Matches[0].Trim();break }
            Start-Sleep -Milliseconds 75
        } while([DateTime]::UtcNow -lt $deadline)
        $expectedView=if($Same){'SameChat'}else{'OtherChat'}
        if($result -notmatch "focused=False centered=True .*keyboardWindow=unchanged .*view=$expectedView$"){throw "Expected a centered passive completion in $expectedView. Actual: $result"}
        $caption='浮笺提醒'
        $observeMs=if($Same){1250}else{10000}
        $check=[Diagnostics.Stopwatch]::StartNew()
        do {
            $card=[CompletionTestWindow]::FindWindow([System.Management.Automation.Language.NullString]::Value,$caption)
            $visible=$card -ne [IntPtr]::Zero -and [CompletionTestWindow]::IsWindowVisible($card)
            if(!$visible){throw "Unexpected window visibility: $visible"}
            $front=[CompletionTestWindow]::GetForegroundWindow()
            $focus=[CompletionTestWindow]::Focus($front)
            if($front -ne $window -or $focus -ne $keyboard){
                $owner=if($front -ne [IntPtr]::Zero){
                    $element=[System.Windows.Automation.AutomationElement]::FromHandle($front)
                    (Get-Process -Id $element.Current.ProcessId -ErrorAction SilentlyContinue).ProcessName
                }
                throw "Focus changed while monitoring the completion. Expected window=$window input=$keyboard; observed window=$front input=$focus process=$owner."
            }
            Start-Sleep -Milliseconds 75
        } while($check.ElapsedMilliseconds -lt $observeMs)
        Write-Output "PASS $result; foreground and focused control unchanged."
        if($Same){
            Start-Process -FilePath $Exe -ArgumentList '--dismiss-preview' -WindowStyle Hidden -Wait
        }elseif($TestInterruption){
            if($result -notmatch 'turn=([0-9a-f-]+)'){throw 'Missing completion preview ID.'}
            Verify-Interruption $reader $Matches[1]
        }
    } finally { $stream.Dispose() }
}
Write-Output 'WAIT_SOURCE_CHAT'
if($ComparisonChat -ne [guid]::Empty) {
    if($ComparisonChat -eq $Chat -or !$names.ContainsKey($ComparisonChat.ToString())){throw 'A distinct indexed comparison chat is required.'}
    Start-Process ('codex://threads/'+$Chat.ToString())
}
Verify-View $true
Write-Output 'WAIT_OTHER_CHAT'
if($ComparisonChat -eq [guid]::Empty){ Verify-View $false }
else {
    Start-Process ('codex://threads/'+$ComparisonChat.ToString())
    try { Verify-View $false $ComparisonChat }
    finally { Start-Process ('codex://threads/'+$Chat.ToString()) }
    $null=Wait-ForChat $true $Chat
    $deadline=[DateTime]::UtcNow.AddSeconds(2)
    do {
        $card=[CompletionTestWindow]::FindWindow([System.Management.Automation.Language.NullString]::Value,'浮笺提醒')
        if(!$card -or ![CompletionTestWindow]::IsWindowVisible($card)){break}
        Start-Sleep -Milliseconds 75
    } while([DateTime]::UtcNow -lt $deadline)
    if([CompletionTestWindow]::IsWindowVisible($card)){throw 'Completion remained visible after returning to its source.'}
    Write-Output 'RESTORED_SOURCE_CHAT; completion is no longer visible.'
}
