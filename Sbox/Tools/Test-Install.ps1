#Requires -Version 7.0
$ErrorActionPreference='Stop'
$package=(Resolve-Path "$PSScriptRoot/..").Path
$project=Join-Path $package ('Art/build/installer-tests/with spaces/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $project|Out-Null
# This is an installer filesystem fixture, never opened in the editor.
[IO.File]::WriteAllText((Join-Path $project '.sbproj'),'{}')
[IO.File]::WriteAllText((Join-Path $project '.loreignore'),"/Art/build/`n")
function Assert($condition,$message){if(!$condition){throw $message}}
function Install{& "$package/Install.ps1" -Project $project}
function Assert-Deployment{
    $source=Join-Path $package 'Editor/AIGuide'
    $destination=Join-Path $project 'Editor/AIGuide'
    $expected=@(Get-ChildItem -LiteralPath $source -File -Recurse)
    Assert (@(Get-ChildItem -LiteralPath $destination -File -Recurse).Count -eq $expected.Count) 'Unexpected deployed files'
    foreach($file in $expected){$target=Join-Path $destination ([IO.Path]::GetRelativePath($source,$file.FullName));Assert ((Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $target).Hash) "Deployment differs: $target"}
}
$initialIgnore=[IO.File]::ReadAllText("$project/.loreignore")
& "$package/Install.ps1" -Project $project -Action Check
& "$package/Install.ps1" -Project $project -DryRun
Assert (!(Test-Path -LiteralPath "$project/Editor") -and !(Test-Path -LiteralPath "$project/.ai_guide") -and !(Test-Path -LiteralPath "$project/Art")) 'Check/dry run wrote files'
Assert ([IO.File]::ReadAllText("$project/.loreignore") -ceq $initialIgnore) 'Check changed project settings'
Write-Output 'PASS read-only check and dry run'
Install
Assert-Deployment
$manifest=Get-Content -LiteralPath "$project/.ai_guide/install.json" -Raw|ConvertFrom-Json
Assert ($manifest.repository -eq 'https://github.com/RailgunHamster/AIGuide.git' -and $manifest.package -eq 'Sbox' -and $manifest.revision) 'Install provenance'
Write-Output 'PASS clean deployment and Git provenance'

$builtin=(Get-ChildItem -LiteralPath "$package/Editor/AIGuide/Recipes" -File -Filter '*.json'|Select-Object -First 1).Name
[IO.File]::WriteAllText("$project/Editor/AIGuide/Recipes/$builtin",'{"title":"custom override","steps":[]}')
[IO.File]::WriteAllText("$project/Editor/AIGuide/Recipes/custom.json",'{"title":"legacy custom","steps":[]}')
[IO.File]::WriteAllText("$project/Editor/AIGuide/obsolete.cs",'local old source')
$oldManifest=Get-Content -LiteralPath "$project/.ai_guide/install.json" -Raw|ConvertFrom-Json -AsHashtable
$oldManifest.files['obsolete.cs']=(Get-FileHash -LiteralPath "$project/Editor/AIGuide/obsolete.cs").Hash
[IO.File]::WriteAllText("$project/.ai_guide/install.json",($oldManifest|ConvertTo-Json -Depth 5))
[IO.File]::WriteAllText("$project/.ai_guide/config.json",'{"port":7669}')
Install
Assert-Deployment
Assert ((Get-Content -LiteralPath "$project/Config/AIGuide/Recipes/$builtin" -Raw|ConvertFrom-Json).title -eq 'custom override') 'Edited built-in was lost'
Assert ((Get-Content -LiteralPath "$project/Config/AIGuide/Recipes/custom.json" -Raw|ConvertFrom-Json).title -eq 'legacy custom') 'Legacy recipe was lost'
Assert ((Get-Content -LiteralPath "$project/.ai_guide/config.json" -Raw|ConvertFrom-Json).port -eq 7669) 'Machine config changed'
Assert (@(Get-ChildItem -LiteralPath "$project/Art/build/aiguide-installs" -Filter obsolete.cs -Recurse -File).Count -eq 1) 'Old edits were not backed up'
Write-Output 'PASS custom recipe migration, obsolete file retirement and local config preservation'

Install
Assert-Deployment
$ignore=Get-Content -LiteralPath "$project/.loreignore"
foreach($rule in @('/Editor/AIGuide/','/.ai_guide/','/.ai_guide_bridge/')){Assert (@($ignore|Where-Object {$_ -ceq $rule}).Count -eq 1) "Duplicate or missing ignore rule: $rule"}
Write-Output 'PASS repeat install'

$hostPath="$project/Editor/AIGuide/GuideHost.cs"
$original=[IO.File]::ReadAllBytes($hostPath)
[IO.File]::AppendAllText($hostPath,"`n// project local edit`n")
$edited=(Get-FileHash -LiteralPath $hostPath).Hash
$rejected=$false
try{Install}catch{if($_.Exception.Message -notlike 'Local plugin edits*'){throw};$rejected=$true}
Assert ($rejected -and (Get-FileHash -LiteralPath $hostPath).Hash -eq $edited) 'Local C# edits were overwritten'
[IO.File]::WriteAllBytes($hostPath,$original)
Write-Output 'PASS managed local source protection'

$beforeIgnore=[IO.File]::ReadAllBytes("$project/.loreignore")
$beforeManifest=[IO.File]::ReadAllBytes("$project/.ai_guide/install.json")
$rejected=$false
try{& "$package/Install.ps1" -Project $project -Verify -Engine $project}catch{if($_.Exception.Message -notlike 'sbox-cli is missing:*'){throw};$rejected=$true}
Assert $rejected 'Broken verification unexpectedly succeeded'
Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes("$project/.loreignore")) -eq [Convert]::ToBase64String($beforeIgnore)) 'Failure did not restore ignore file'
Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes("$project/.ai_guide/install.json")) -eq [Convert]::ToBase64String($beforeManifest)) 'Failure did not restore manifest'
Assert-Deployment
Write-Output 'PASS verification failure rolls back deployment and settings'

[IO.File]::WriteAllText("$project/Editor/AIGuide/Recipes/custom.json",'{"title":"conflicting edit","steps":[]}')
$before=(Get-FileHash -LiteralPath "$project/Editor/AIGuide/Recipes/custom.json").Hash
$backups=@(Get-ChildItem -LiteralPath "$project/Art/build/aiguide-installs" -Directory).Count
$rejected=$false
try{Install}catch{if($_.Exception.Message -notlike 'Custom tutorial conflict:*'){throw};$rejected=$true}
Assert $rejected 'Conflicting custom recipe must be rejected'
Assert ((Get-FileHash -LiteralPath "$project/Editor/AIGuide/Recipes/custom.json").Hash -eq $before) 'Conflict modified deployment'
Assert (@(Get-ChildItem -LiteralPath "$project/Art/build/aiguide-installs" -Directory).Count -eq $backups) 'Conflict must fail before installation'
Write-Output 'PASS conflict preflight preserves both copies'
Write-Output "7/7 installer checks passed; fixture retained at $project"
