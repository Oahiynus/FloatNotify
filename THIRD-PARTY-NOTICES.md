# 浮笺 FloatNotify 1.0.3 许可与第三方声明

浮笺自有源码、脚本和文档由 Oahiynus 以 [MIT 许可证](LICENSE) 提供。复制或分发其全部或实质部分时，请保留版权声明和许可全文。以下列出单独保留权利的素材和随 Windows x64 安装包提供的第三方组件。

## 浮页图标

浮页图标（`SettingsUI/folio.png` 和 `SettingsUI/folio.ico`）© 2026 Oahiynus，保留所有权利。浮笺的 MIT 许可不涵盖图标，包括程序、安装器和文档截图中呈现的图标。单独复用、修改或再分发该图标，请向 Oahiynus 取得许可。

## 随安装包提供的组件

| 组件 | 使用方式 | 随附声明 |
| --- | --- | --- |
| Microsoft Edge WebView2 SDK 1.0.3537.50 | 包含 x64 Loader 和 Core、WinForms、Wpf 三个托管 DLL。该版本采用 BSD 三条款形式的微软许可，NOTICE 含 ANTLR 和 StringTemplate 的声明。 | [WebView2 许可](Notices/WebView2-LICENSE.txt) · [WebView2 第三方声明](Notices/WebView2-NOTICE.txt) |
| .NET 10 Windows x64 apphost | `FloatNotify.exe` 含 .NET 原生启动器。附上 .NET Foundation and Contributors 的 MIT 许可和本次使用的 .NET 10.0.8 上游第三方声明。 | [.NET 启动器许可](Notices/dotnet-apphost-LICENSE.txt) · [.NET 上游第三方声明](Notices/dotnet-THIRD-PARTY-NOTICES.txt) |
| Microsoft C++/WinRT 与 Visual C++ | `GlassBackdrop.dll` 使用 Windows SDK 的 C++/WinRT 头文件、MSVC 标准库并静态链接 C/C++ 运行库。C++/WinRT 使用 MIT，微软 C++ 标准库使用 Apache 2.0 与 LLVM 例外；MSVC 运行库按相应工具链的可再分发代码条款提供。 | [C++/WinRT 许可](Notices/CppWinRT-LICENSE.txt) · [MSVC 标准库许可与例外](Notices/MSVC-STL-LICENSE.txt) · [微软分发说明](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files) |
| NSIS 3.13 | Windows 安装器和卸载器使用 NSIS、内置插件及 LZMA 压缩。NSIS 的 COPYING 列出主体组件许可、压缩模块许可和 LZMA 链接例外。 | [NSIS 随附的 COPYING](Notices/NSIS-COPYING.txt) |

.NET Desktop Runtime 和 WebView2 Evergreen Runtime 由用户系统提供。设置界面按名称使用 Windows 系统字体。安装包未附带上述完整运行时或字体文件。
