@echo off
rem Builds app\ZirisWorkshop.exe with the C# compiler that ships with Windows.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist app mkdir app
"%CSC%" /nologo /target:winexe /optimize+ /out:app\ZirisWorkshop.exe ^
  /win32icon:assets\icon.ico /resource:assets\icon.ico,icon.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:"%FW%\System.Web.Extensions.dll" /r:"%FW%\System.IO.Compression.dll" ^
  /r:"%FW%\System.IO.Compression.FileSystem.dll" /r:Microsoft.VisualBasic.dll /r:Microsoft.CSharp.dll ^
  src\ZirisWorkshop.cs
exit /b %ERRORLEVEL%
