# 从源码构建浮笺

在 Windows x64 上准备 PowerShell 7、.NET 10 SDK、Visual Studio 的 MSVC x64 C++ 构建工具，以及带有 C++/WinRT 头文件的 Windows SDK。首次还原 WebView2 SDK 包需要访问 NuGet，打开设置窗口需要 Microsoft Edge WebView2 Runtime。

## 构建与自检

从仓库根目录运行以下命令。项目将编译桌面程序和玻璃渲染 DLL，并复制设置界面资源。

```powershell
Set-Location ./src/FloatNotify
dotnet publish ./FloatNotify.csproj -c Release -r win-x64 --no-self-contained -o ./dist
dotnet ./dist/FloatNotify.dll --self-test
```

自检成功时，最后一行显示 `PASS`。

## 生成并复核 x64 安装包

继续在源码目录执行。打包需要 NSIS 3 Unicode 编译器。解包复核需要 Python 3.9 或以上版本，以及支持 NSIS 的 7-Zip。示例从 PATH 查找工具，便携安装也可以直接指定其可执行文件路径。

```powershell
$compiler = (Get-Command makensis.exe).Source
$sevenZip = (Get-Command 7z.exe).Source
$run = Join-Path ([IO.Path]::GetTempPath()) ('FloatNotify-build-' + [guid]::NewGuid().ToString('N'))
$build = Join-Path $run 'stage'
$output = Join-Path $run 'out'
./packaging/Build.ps1 -Compiler $compiler -BuildDirectory $build -OutputDirectory $output
$setup = (Get-ChildItem -LiteralPath $output -File -Filter '*-Setup.exe').FullName
python ./packaging/verify_package.py $setup $build $sevenZip
Get-FileHash -LiteralPath $setup -Algorithm SHA256
```

`Build.ps1` 需要全新的暂存目录。它会重新编译程序、运行自检，按 `packaging/files.txt` 装入应用、项目许可证和第三方声明。仓库根目录的 `LICENSE`、`THIRD-PARTY-NOTICES.md` 与 `Notices/` 也会随直接发布的程序输出。打包时会将 WebView2 的两份声明与实际还原的 NuGet 包逐字节比对。`verify_package.py` 解包核对文件清单、逐文件哈希、架构和本机构建路径。将本次验证用的安装包和已发布安装包分别保存。

## 事件来源与桌面复核

浮笺通过 Codex 的 `PermissionRequest` 和 `Stop` Hook 处理人工审批和完成提醒，并增量读取本地桌面日志与会话记录，识别提问和处理回执。桌面表单通过 Windows 辅助功能读取。应用设置、提醒历史和诊断文件位于当前用户的 `%LOCALAPPDATA%\FloatNotify`。

接入验证保存在同目录的 `connection.json`。审批记录来自官方审批请求，问答记录来自桌面日志或会话事件，完成验证需要同时收到 Stop 和真实完成事件。修改某类事件的识别或处理约定时，同步更新 `ConnectionVerification.cs` 中对应的处理版本。设置和界面调整沿用原处理版本。

启动应用后，可通过 `python ./verify_windows.py <FloatNotify.exe 的完整路径>` 复核三类卡片的位置和前台焦点。该脚本需要 Python 和 Tk，会短暂显示测试卡片，并最小化、恢复 Codex。要验证对话级的完成提示，运行 `./Verify-ChatCompletion.ps1 -Exe <程序路径> -Chat <测试对话编号>`。
