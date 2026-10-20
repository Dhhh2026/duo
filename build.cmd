@echo off
rem 用 Win11 自带 .NET Framework 4.8 编译器直接产出单文件 DUO.exe（无第三方依赖）
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /utf8output /target:winexe /platform:x64 /optimize+ /win32icon:icon.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  /out:DUO.exe Program.cs Native.cs Hid.cs Drivers.cs Config.cs TrayRenderer.cs DuoApp.cs MainForm.cs
if errorlevel 1 pause
