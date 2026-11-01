@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
set DIA=C:\Program Files\Microsoft Visual Studio\2022\Community\DIA SDK
cd /d C:\cbot\pdbdump
cl /nologo /EHsc /O2 /I"%DIA%\include" pdbdump.cpp /link /LIBPATH:"%DIA%\lib\amd64" diaguids.lib ole32.lib oleaut32.lib advapi32.lib
copy /y "%DIA%\bin\amd64\msdia140.dll" . >nul
