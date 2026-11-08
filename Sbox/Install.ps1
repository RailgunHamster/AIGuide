#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Project)
$ErrorActionPreference='Stop'
$projectRoot=(Resolve-Path -LiteralPath $Project).Path.TrimEnd('\','/')
if(!(Test-Path -LiteralPath (Join-Path $projectRoot '.sbproj'))){throw 'Project must contain .sbproj'}
$repoRoot=(Resolve-Path "$PSScriptRoot/..").Path
$revision=(& git -C $repoRoot rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0){throw 'Run the installer from a checkout of the existing AIGuide repository'}
$dirty=!!(& git -C $repoRoot status --porcelain -- Sbox)
if($LASTEXITCODE -ne 0){throw 'Cannot read AIGuide checkout status'}
$source=(Resolve-Path -LiteralPath "$PSScriptRoot/Editor/AIGuide").Path
$destination=[IO.Path]::GetFullPath((Join-Path $projectRoot 'Editor/AIGuide'))
if(!$destination.StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $destination -eq $source){throw 'Invalid deployment destination'}
if((Test-Path -LiteralPath $destination) -and (Get-Item -LiteralPath $destination).LinkType){throw 'Deployment must be a regular directory, not a link'}

# Check all legacy tutorial conflicts before changing the deployment or project tutorials.
$projectRecipes=Join-Path $projectRoot 'Config/AIGuide/Recipes'
$oldRecipes=Join-Path $destination 'Recipes'
$migrate=@()
if(Test-Path -LiteralPath $oldRecipes){foreach($file in Get-ChildItem -LiteralPath $oldRecipes -File -Filter '*.json'){
    $builtin=Join-Path $source ('Recipes/'+$file.Name)
    if(!(Test-Path -LiteralPath $builtin) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $builtin).Hash){
        $custom=Join-Path $projectRecipes $file.Name
        if(Test-Path -LiteralPath $custom){if((Get-FileHash -LiteralPath $custom).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw "Custom tutorial conflict: $custom"}}
        else{$migrate+=@{source=$file.FullName;target=$custom}}
    }
}}

$files=[ordered]@{}
foreach($file in Get-ChildItem -LiteralPath $source -Recurse -File|Sort-Object FullName){
    $relative=[IO.Path]::GetRelativePath($source,$file.FullName).Replace('\','/')
    $files[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
$backup=Join-Path $projectRoot ('Art/build/aiguide-installs/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $backup|Out-Null
$staged=Join-Path $backup 'new'
Copy-Item -LiteralPath $source -Destination $staged -Recurse
foreach($entry in $migrate){
    New-Item -ItemType Directory -Force -Path $projectRecipes|Out-Null
    Copy-Item -LiteralPath $entry.source -Destination $entry.target
}
# Keep all old files and local edits outside the compiled Editor directory.
# A fresh deployment also retires obsolete C# files and migrated legacy recipes.
$previous=Join-Path $backup 'AIGuide'
if(Test-Path -LiteralPath $destination){Move-Item -LiteralPath $destination -Destination $previous}
try{
    New-Item -ItemType Directory -Force -Path (Split-Path $destination)|Out-Null
    Move-Item -LiteralPath $staged -Destination $destination
}catch{
    if(Test-Path -LiteralPath $previous){Move-Item -LiteralPath $previous -Destination $destination}
    throw
}

$ignore=if(Test-Path -LiteralPath (Join-Path $projectRoot '.loreignore')){Join-Path $projectRoot '.loreignore'}else{Join-Path $projectRoot '.gitignore'}
$text=if(Test-Path -LiteralPath $ignore){[IO.File]::ReadAllText($ignore)}else{''}
foreach($rule in @('/Editor/AIGuide/','/.ai_guide/','/.ai_guide_bridge/')){if(($text -split '\r?\n') -notcontains $rule){$text+="`n$rule`n"}}
[IO.File]::WriteAllText($ignore,$text)
$data=Join-Path $projectRoot '.ai_guide';New-Item -ItemType Directory -Force -Path $data|Out-Null
$manifest=@{repository='https://github.com/RailgunHamster/AIGuide.git';revision=$revision;dirty=$dirty;package='Sbox';files=$files}
[IO.File]::WriteAllText((Join-Path $data 'install.json'),($manifest|ConvertTo-Json -Depth 5))
Write-Output "Installed AIGuide/Sbox at $destination ($revision; local changes: $dirty)."
Write-Output "Previous deployment backed up at $backup. Restart the editor to load the installed plugin."
