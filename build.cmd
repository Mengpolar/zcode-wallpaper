@echo off
rem Build ZCodeWallpaper.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem No SDK required. Output goes to dist\ZCodeWallpaper.exe
rem Note: the window/taskbar icon is applied only when dist\zcode.ico exists
rem       (the icon is not distributed in this repo; see README).

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set ROOT=%~dp0

if not exist "%ROOT%dist" mkdir "%ROOT%dist"

set ICON_ARG=
if exist "%ROOT%dist\zcode.ico" set ICON_ARG=/win32icon:"%ROOT%dist\zcode.ico"

"%CSC%" /nologo /target:winexe /platform:anycpu /out:"%ROOT%dist\ZCodeWallpaper.exe" %ICON_ARG% /r:System.Windows.Forms.dll /r:System.Drawing.dll "%ROOT%src\Program.cs"
