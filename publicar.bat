@echo off
rem Gera o ARAUJO CONTROL SUITE pronto para uso (nao precisa instalar o .NET).
rem Saida: ..\ResetExplode-dist\AraujoControlSuite.exe + LEIA-ME.txt
rem Sempre recompila do zero a partir do codigo atual desta pasta.
cd /d "%~dp0"
echo Fechando o AraujoControlSuite se estiver aberto ...
taskkill /f /im AraujoControlSuite.exe >nul 2>&1
taskkill /f /im ResetExplode.exe >nul 2>&1
echo Gerando AraujoControlSuite.exe a partir do codigo atual ...
rmdir /s /q obj\Release >nul 2>&1
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "..\ResetExplode-dist" -nologo -v q
if errorlevel 1 (
    echo.
    echo FALHOU. Veja os erros acima. O .exe antigo NAO foi atualizado.
    pause
    exit /b 1
)
copy /y "LEIA-ME.txt" "..\ResetExplode-dist\LEIA-ME.txt" >nul
echo.
echo Pronto: %~dp0..\ResetExplode-dist
for %%F in ("..\ResetExplode-dist\AraujoControlSuite.exe") do echo Arquivo gerado em %%~tF (confira se e de agora^)
pause
