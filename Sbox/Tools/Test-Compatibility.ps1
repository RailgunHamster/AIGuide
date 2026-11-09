#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Engine,
    [string]$LegacyTools
)
$ErrorActionPreference='Stop'
$package=(Resolve-Path "$PSScriptRoot/..").Path
$output=Join-Path $package ('Art/build/compatibility/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $output|Out-Null
function Xml([string]$Value){[Security.SecurityElement]::Escape($Value)}
$common='<TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><GenerateAssemblyInfo>false</GenerateAssemblyInfo><AllowUnsafeBlocks>true</AllowUnsafeBlocks>'
$unit=Join-Path $output 'unit';New-Item -ItemType Directory -Force -Path $unit|Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>$common<OutputType>Exe</OutputType></PropertyGroup><ItemGroup>
<Compile Include="$(Xml "$package/Editor/AIGuide/GuideCompatibility.cs")" />
<Compile Include="$(Xml "$PSScriptRoot/CompatibilityChecks.cs")" />
</ItemGroup></Project>
"@|Set-Content -LiteralPath "$unit/test.csproj" -Encoding utf8
& dotnet run --project "$unit/test.csproj" -- "$package/Editor/AIGuide/Native/BuildInfo.json"
if($LASTEXITCODE){throw 'API and Qt profile compatibility checks failed'}
$targets=[Collections.Generic.List[object]]::new()
foreach($directory in $Engine){$targets.Add(@{engine=(Resolve-Path -LiteralPath $directory).Path;legacy=$false})}
if($LegacyTools){$targets.Add(@{engine=(Resolve-Path -LiteralPath $Engine[0]).Path;legacy=$true})}
$index=0
foreach($target in $targets){
    $build=Join-Path $output ('compile-'+(++$index));New-Item -ItemType Directory -Force -Path $build|Out-Null
    $references=foreach($file in Get-ChildItem -LiteralPath "$($target.engine)/bin/managed" -File -Filter '*.dll'){
        if($file.Name -like 'System.*' -or $file.Name -in @('netstandard.dll','mscorlib.dll')){continue}
        $path=$file.FullName
        if($target.legacy -and (Test-Path -LiteralPath (Join-Path $LegacyTools $file.Name))){$path=Join-Path $LegacyTools $file.Name}
        '<Reference Include="'+(Xml $file.BaseName)+'"><HintPath>'+(Xml $path)+'</HintPath><Private>false</Private></Reference>'
    }
    $sources=@('<Compile Include="'+(Xml "$package/Editor/AIGuide/*.cs")+'" />')
    @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>$common<AssemblyName>AIGuide.Compatibility.$index</AssemblyName></PropertyGroup><ItemGroup>
<Using Include="Sandbox.Internal.GlobalToolsNamespace" Static="true" />
<Using Include="Sandbox.Internal.GlobalGameNamespace" Static="true" />
$($references -join "`n")
$($sources -join "`n")
</ItemGroup></Project>
"@|Set-Content -LiteralPath "$build/test.csproj" -Encoding utf8
    & dotnet build "$build/test.csproj" --nologo -v minimal
    if($LASTEXITCODE){throw "Plugin compile failed against $($target.engine); legacy overrides: $($target.legacy)"}
    Write-Output "PASS full plugin compile: $($target.engine); legacy overrides: $($target.legacy)"
}
Write-Output "Passed helper checks and $($targets.Count) full plugin SDK compilations. Artifacts: $output"
