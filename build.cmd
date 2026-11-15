@echo off
rem Builds AudioSpeech.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:AudioSpeech.exe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll AudioSpeech.cs Ui.cs || exit /b 1
"%~dp0AudioSpeech.exe" --selftest && echo Built AudioSpeech.exe
