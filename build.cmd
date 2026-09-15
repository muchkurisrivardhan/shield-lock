@echo off
rem Builds ShieldLock.exe with the C# compiler that ships with Windows (.NET Framework 4.8) - no SDK needed.
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /out:ShieldLock.exe /resource:ShieldAlert.xaml ^
 /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
 /r:"%FW%\System.Xaml.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ^
 Program.cs
