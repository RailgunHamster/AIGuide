param([Parameter(Mandatory)][string]$QtSdk,[Parameter(Mandatory)][string]$EngineBin,[string]$VsTools='C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat')
$ErrorActionPreference='Stop'
$QtSdk=(Resolve-Path -LiteralPath $QtSdk).Path
$projectRoot=(Resolve-Path -LiteralPath "$PSScriptRoot/../../..").Path
$output=Join-Path $projectRoot 'Art/build/aiguide-native'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$dll=Join-Path $PSScriptRoot 'aiguide_qt5.dll'
$cpp=Join-Path $PSScriptRoot 'aiguide_qt.cpp'
$EngineBin=(Resolve-Path -LiteralPath $EngineBin).Path
$dump=(Get-ChildItem "$(Split-Path $VsTools)/../../Tools/MSVC/*/bin/Hostx64/x64/dumpbin.exe"|Select-Object -Last 1).FullName
if(!$dump){throw 'dumpbin.exe not found beside the selected Visual C++ installation'}
$imports=''
foreach($module in @('Qt5Core','Qt5Gui','Qt5Widgets')){
    $exports=& $dump /exports "$EngineBin/$module.dll"
    $names=@($exports|ForEach-Object{if($_ -match '^\s+[0-9]+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)'){$Matches[1]}})
    if($names.Count -lt 100){throw "Cannot read exports of $module.dll"}
    [IO.File]::WriteAllLines("$output/$module.def",@("LIBRARY $module.dll",'EXPORTS')+$names)
    $imports+="lib.exe /nologo /def:`"$output\$module.def`" /machine:x64 /out:`"$output\$module.lib`"`r`nif errorlevel 1 exit /b 1`r`n"
}
$script=@"
@echo off
call "$VsTools" >nul
if errorlevel 1 exit /b 1
$imports
cl.exe /nologo /std:c++17 /EHsc /MD /O2 /c "$PSScriptRoot\QtAllocator.cpp" /Fo"$output\QtAllocator.obj"
if errorlevel 1 exit /b 1
cl.exe /nologo /std:c++17 /EHsc /MD /O2 /LD /DQT_NO_DEBUG /DQT_CORE_LIB /DQT_GUI_LIB /DQT_WIDGETS_LIB /I"$PSScriptRoot\QtHeaders" /I"$QtSdk\include" /I"$QtSdk\include\QtCore" /I"$QtSdk\include\QtGui" /I"$QtSdk\include\QtWidgets" "$cpp" "$output\QtAllocator.obj" /Fo"$output\aiguide_qt.obj" /link /LIBPATH:"$output" Qt5Widgets.lib Qt5Gui.lib Qt5Core.lib User32.lib /MAP:"$output\aiguide_qt.map" /OUT:"$dll" /IMPLIB:"$output\aiguide_qt.lib"
"@
$cmd=Join-Path $output 'build.cmd'
[IO.File]::WriteAllText($cmd,$script)
& $env:ComSpec /d /c $cmd
if($LASTEXITCODE -ne 0){throw 'AI Guide Qt adapter build failed'}
$modules=@{}
foreach($module in @('Qt5Core.dll','Qt5Gui.dll','Qt5Widgets.dll')){$modules[$module]=(Get-FileHash -Algorithm SHA256 -LiteralPath "$EngineBin/$module").Hash}
$info=@{qt='5.15.2';qt_headers_commit='fc20424f10a799a654d2eecc9539aa6fc587d046';modules=$modules;adapter_sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $dll).Hash}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'BuildInfo.json'),($info|ConvertTo-Json -Depth 4))
