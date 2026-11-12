#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Project,[string]$BaseUrl='http://127.0.0.1:7660/mcp',[switch]$SkipHammer,[switch]$KeepFixture,[string]$ResultsPath)
$ErrorActionPreference='Stop'
$project=(Resolve-Path -LiteralPath $Project).Path
if(@(Get-ChildItem -LiteralPath $project -File -Force -Filter '*.sbproj').Count -ne 1){throw 'Pass -Project with the installed s&box project directory.'}
if(!$ResultsPath){$ResultsPath=Join-Path $project 'Art/build/aiguide-tests/results.json'}
New-Item -ItemType Directory -Force -Path (Split-Path $ResultsPath) | Out-Null
$results=[Collections.Generic.List[object]]::new();$script:requestId=0
function Rpc($method,$parameters=@{}){
    $body=@{jsonrpc='2.0';id=++$script:requestId;method=$method;params=$parameters}|ConvertTo-Json -Depth 60 -Compress
    $reply=Invoke-RestMethod $BaseUrl -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 120
    if($reply.error){throw ($reply.error|ConvertTo-Json -Compress)}
    return $reply.result
}
function Tool($name,$arguments=@{}){
    $r=Rpc 'tools/call' @{name=$name;arguments=$arguments}
    $text=($r.content|Where-Object type -eq text|Select-Object -First 1).text
    if($r.isError){throw "$name : $text"}
    if($null -ne $text){return $text|ConvertFrom-Json -Depth 80}
    return $r
}
function Exec($code){Tool editor_exec @{code=$code}}
function Native($command,$arguments=@{}){
    $argJson=($arguments|ConvertTo-Json -Depth 50 -Compress).Replace('"','""')
    Exec ('var t=typeof(AIGuide.Sbox.GuideHost).Assembly.GetType("AIGuide.Sbox.GuideNative");return t.GetMethod("Call",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).Invoke(null,new object[]{"'+$command+'",System.Text.Json.Nodes.JsonNode.Parse(@"'+$argJson+'").AsObject()});')
}
function Assert($condition,$message){if(!$condition){throw $message}}
function Test($name,[scriptblock]$body){$watch=[Diagnostics.Stopwatch]::StartNew();try{&$body|Out-Null;$results.Add(@{name=$name;passed=$true;ms=$watch.ElapsedMilliseconds});Write-Output "PASS $name"}catch{$results.Add(@{name=$name;passed=$false;error=$_.Exception.Message;ms=$watch.ElapsedMilliseconds});Write-Output "FAIL $name : $($_.Exception.Message)"}}
function Find-One($query){if($script:fixture -and !$query.ContainsKey('under')){$query.under=$script:fixture.ref};$found=Tool ui_find $query;Assert ($found.total -eq 1) "Expected unique target: $($query|ConvertTo-Json -Compress); found $($found.total)";return $found.elements[0]}
function Act($action,$target,$extra=@{}){$parameters=@{action=$action;target=$target};foreach($entry in $extra.GetEnumerator()){$parameters[$entry.Key]=$entry.Value};Tool ui_act $parameters}
function Wait-Guide($state,[int]$index=-1){$until=[DateTime]::UtcNow.AddSeconds(5);do{$s=Tool guide_status;if($s.state -eq $state -and ($index -lt 0 -or $s.index -eq $index)){return $s};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $until);throw "Guide did not reach $state/$index : $($s|ConvertTo-Json -Depth 3 -Compress)"}
$fixture=$null;$token=[Guid]::NewGuid().ToString('N');$baseline=Join-Path $project "Tools/aiguide-test-$token.txt";$created=Join-Path $project "Tools/aiguide-created-$token.txt";$recipe="integration-$token"
New-Item -ItemType Directory -Force -Path (Join-Path $project 'Tools')|Out-Null
$localOnly=Join-Path $project ".aiguide-test-state/$token.txt"
try{
    Test 'MCP initialize, 17 guide schemas and optional debugger schemas' {Assert ((Rpc initialize @{protocolVersion='2025-11-25'}).serverInfo.name -eq 'sbox-ai-guide') 'server name';$expected=if(Test-Path -LiteralPath "$project/Libraries/AIDebug/.sbproj"){36}else{17};Assert ((Rpc 'tools/list').tools.Count -eq $expected) 'tool count'}
    $initial=Tool editor_state;if($initial.ui.modal){throw 'Close the pre-existing editor modal before running tests; it is not a test fixture.'}
    Test 'editor context and scene hierarchy' {Assert ((Tool editor_state).project -eq $project) 'project';Assert ((Tool scene_tree @{depth=1}).children.Count -gt 0) 'scene tree'}
    Test 'project guide panel layout and recipe button' {
        Exec 'AIGuide.Sbox.GuideHost.ShowPanel();return true;'
        $panel=Tool ui_find @{class='GuidePanel';kind='control'};Assert ($panel.total -eq 1) 'panel window'
        $stop=Tool ui_find @{text='停止当前引导';class='Button';kind='control';under=$panel.elements[0].ref};Assert ($stop.total -eq 1 -and $stop.elements[0].visible) 'panel controls must be in the window canvas'
        Exec 'foreach(var w in Editor.Window.All.Where(w=>w.GetType().FullName=="AIGuide.Sbox.GuidePanel").ToArray())w.Close();return true;'
    }
    Test 'C# execution and diagnostics' {Assert ((Exec 'return 21*2;') -eq 42) 'C# result';$failed=$false;try{Exec 'not C# syntax'}catch{$failed=$true};Assert $failed 'invalid script was accepted'}
    Test 'native fixture and structured snapshot' {Native test_close;$script:fixture=Native test_fixture;Assert ((Tool ui_snapshot @{root=$fixture.ref}).total -gt 12) 'snapshot'}
    $button=Find-One @{name='guideIncrement';kind='control'};$edit=Find-One @{name='guideText';kind='control'}
    Test 'Unicode input and persistent text locator' {Act type $edit.ref @{text='月球 🐣 Guide 42'};Assert ((Tool ui_describe @{target=$edit.locator}).text -eq '月球 🐣 Guide 42') 'Unicode typing'}
    Test 'paste, focus and shortcut' {Act paste $edit.locator @{text='replacement'};Act key $edit.locator @{keys='Ctrl+A'};Act type $edit.locator @{text='新文本'};Assert ((Tool ui_describe @{target=$edit.locator}).text -eq '新文本') 'paste/key/focus'}
    Test 'native click and hover' {Act hover $button.locator;Act click $button.locator;Assert ((Find-One @{name='guideCount';kind='control'}).text -eq 'Count: 1') 'button result'}
    Test 'window coordinate hit-test and target coordinate override' {
        $x=$button.rect[0]-$fixture.rect[0]+$button.rect[2]/2;$y=$button.rect[1]-$fixture.rect[1]+$button.rect[3]/2
        Tool ui_act @{action='click';window=$fixture.ref;x=$x;y=$y};Assert ((Find-One @{name='guideCount';kind='control'}).text -eq 'Count: 2') 'window hit-test'
        Act click $button.ref @{x=$x;y=$y};Assert ((Find-One @{name='guideCount';kind='control'}).text -eq 'Count: 3') 'coordinate override'
    }
    Test 'collapsed tree diagnostics and virtual item double-click' {$body=Find-One @{text='Body';kind='tree';exact=$true;visible_only=$false};Assert ((Tool ui_describe @{target=$body.ref}).hidden_because.type -contains 'collapsed_parent') 'collapsed reason';$player=Find-One @{text='Player';kind='tree';exact=$true};Act double_click $player.ref;Assert ((Tool ui_describe @{target=$body.ref}).visible) 'tree expansion'}
    Test 'virtual tab selection and scroll event' {$tab=Find-One @{text='Two';kind='tab';exact=$true};Act click $tab.ref;Assert ((Find-One @{text='Second page';exact=$true;kind='control'}).visible) 'tab selection';Act scroll $edit.ref @{amount=2}}
    Test 'right/middle and slider drag' {foreach($a in @('right_click','middle_click')){Act $a $button.ref};Act key $edit.ref @{keys='Escape'};$slider=Find-One @{name='guideSlider';kind='control'};Act drag $slider.ref @{to=@{x=($slider.rect[0]-$fixture.rect[0]+$slider.rect[2]-8);y=($slider.rect[1]-$fixture.rect[1]+$slider.rect[3]/2)}};Start-Sleep -Milliseconds 300;Assert ([int](Tool ui_describe @{target=$slider.ref}).value -ge 90) 'slider drag value';$bad=$false;try{Act drag $edit.ref @{to=@{name='no-such-destination'}}}catch{$bad=$true};Assert $bad 'invalid drag must reject before mouse down'}
    Test 'native menu and modal-safe state/input' {
      try{
        $menu=Find-One @{text='Test';kind='menu';exact=$true;under=$fixture.ref};Act click $menu.ref
        $open=Find-One @{text='Open modal';kind='menu';exact=$true};Act click $open.ref
        $modal=Find-One @{name='guideModal';kind='control'}
        Assert ((Tool editor_state).ui.windows.text -contains 'AI Guide modal') 'modal state'
        Assert ((Tool editor_exec @{code='var before=Environment.CurrentManagedThreadId;await Task.Delay(30);return before==Environment.CurrentManagedThreadId;';timeout_ms=2000})) 'async C# UI continuation inside modal'
        $blocked=$false;try{Act click $button.ref}catch{$blocked=$true};Assert $blocked 'modal must block parent'
        $input=Find-One @{name='modalText';kind='control'};Act paste $input.ref @{text='模态框可操作'};Assert ((Tool ui_describe @{target=$input.ref}).text -eq '模态框可操作') 'modal input'
        $bridge=Join-Path $project '.ai_guide_bridge';New-Item -ItemType Directory -Force "$bridge/in"|Out-Null;$id=[Guid]::NewGuid().ToString('N');[IO.File]::WriteAllText("$bridge/in/$id.json",'{"jsonrpc":"2.0","id":23,"method":"ping"}');$until=[DateTime]::UtcNow.AddSeconds(5);while(!(Test-Path "$bridge/out/$id.json") -and [DateTime]::UtcNow -lt $until){Start-Sleep -Milliseconds 100};Assert (Test-Path "$bridge/out/$id.json") 'file bridge inside modal'
        $close=Find-One @{name='modalClose';kind='control'};Act click $close.ref
      }finally{Native test_close_modal}
    }
    Test 'PNG screenshot and numbered overlay' {Tool ui_highlight @{targets=@($button.ref,$edit.ref);text='AI Guide 测试';duration_ms=0};$image=Tool editor_screenshot @{window=$fixture.ref};Assert ((Test-Path $image.path) -and $image.width -gt 100) 'screenshot';Tool ui_highlight @{clear=$true}}
    Test 'overlay is composited, click-through and stable across timer ticks' {
        Act focus $edit.ref;Tool ui_highlight @{target=$button.ref;text='稳定高亮';duration_ms=0};Start-Sleep -Milliseconds 200
        $before=Native test_overlay_state;Assert ($before.overlays.Count -eq 1) 'one visible overlay'
        $o=$before.overlays[0];Assert ($o.tool_window -and $o.layered -and $o.input_transparent -and $o.no_activate -and $o.geometry_matches) 'overlay must be a separate click-through compositor window'
        Start-Sleep -Milliseconds 1200;$after=Native test_overlay_state
        Assert ($after.overlays[0].shows -eq $o.shows -and $after.overlays[0].hides -eq $o.hides) 'timer toggled overlay visibility'
        Assert ($after.overlays[0].paints -le $o.paints+1) 'unchanged overlay repeatedly repainted'
        Assert ($after.focus -eq $before.focus) 'timer stole keyboard focus'
        $count=[int]((Find-One @{name='guideCount';kind='control'}).text -replace '\D','');Act click $button.ref
        Assert ([int]((Find-One @{name='guideCount';kind='control'}).text -replace '\D','') -eq $count+1) 'highlighted control must remain clickable'
        Tool ui_highlight @{clear=$true}
    }
    Test 'human click guide and pre-satisfied conditions' {
        Tool ui_guide @{title='交互测试';steps=@(@{target=$button.locator;text='点击计数'},@{target=$edit.locator;text='已有文本，不自动跳过';until=@{text_contains=@{target=$edit.locator;text='新文本'}}})}
        Act click $button.ref;Assert ((Tool guide_status).index -eq 0) 'agent click advanced human step'
        Native test_user_click @{target=$button.ref};Wait-Guide running 1;Start-Sleep -Milliseconds 250;Assert ((Tool guide_status).index -eq 1) 'pre-satisfied condition auto-skipped'
        Tool guide_control @{action='next'};Wait-Guide completed
    }
    Test 'missing parent scope never resolves an unrelated global control' {
        $q=@{name='guideText';under=@{name='this-window-does-not-exist'}}
        Assert ((Tool ui_find $q).total -eq 0) 'missing scope widened to global search'
        Tool ui_guide @{steps=@(@{target=$q;text='等待正确窗口';until='click'})};Start-Sleep -Milliseconds 250
        $s=Tool guide_status;Assert ($s.waiting -and $s.target_error) 'missing target must be diagnosed'
        Native test_user_click @{target=$button.ref};Start-Sleep -Milliseconds 250;Assert ((Tool guide_status).index -eq 0) 'unrelated click advanced missing target';Tool guide_control @{action='stop'}
    }
    Test 'Godot-style fallback and live text completion advance without next' {
        Act paste $edit.ref @{text='before'}
        Tool ui_guide @{steps=@(@{target='/qt/stale';fallback=@{name='guideText'};text='填写内容';until=@{text_contains=@{target=@{name='guideText'};text='done value'}}},@{target=$button.locator;text='手动确认';until='manual'})}
        Start-Sleep -Milliseconds 200;$s=Tool guide_status;Assert ($s.fallback_used -and $s.target.ref -eq $edit.ref) 'fallback did not find correct target'
        Act paste $edit.ref @{text="done `n value"};Wait-Guide running 1
        Tool guide_control @{action='prev'};Start-Sleep -Milliseconds 350;Assert ((Tool guide_status).index -eq 0) 'previous step immediately advanced again'
        Native test_user_click @{target=$edit.ref};Wait-Guide running 1;Tool guide_control @{action='stop'}
    }
    Test 'labelled field and numeric result conditions' {
        $q=@{class='QLineEdit';under=@{class='QFrame';label='Labelled value';under=$fixture.ref}}
        $value=Tool ui_describe @{target=$q};Assert ($value.name -eq 'guideLabelledValue') 'semantic label field'
        Act paste $value.ref @{text='0'};Tool ui_guide @{steps=@(@{target=$q;text='输入 0.7';until=@{number_equals=@{target=$q;value=0.7}}})}
        Act paste $value.ref @{text='0.700'};Wait-Guide completed
    }
    Test 'committed values wait for Enter and project-relative saved result' {
        Act paste $edit.ref @{text='before'}
        Tool ui_guide @{steps=@(@{target=$edit.locator;text='提交后再继续';until=@{text_equals=@{target=$edit.locator;text='committed';committed=$true}}},@{target=$edit.locator;text='保存准确文件';project_path=('Tools/'+[IO.Path]::GetFileName($created));until=@{file_saved=@{path=('Tools/'+[IO.Path]::GetFileName($created));contains=$token}}})}
        Act paste $edit.ref @{text='committed'};Start-Sleep -Milliseconds 300;Assert ((Tool guide_status).index -eq 0) 'typing advanced before commit'
        Act key $edit.ref @{keys='Enter'};Wait-Guide running 1
        $clip=Exec 'return Editor.EditorUtility.Clipboard.Paste();'
        Assert ($clip.Replace('\','/') -eq $created.Replace('\','/')) 'project path clipboard is not portable'
        # A Hammer .vmap is binary DMX: text after NUL must remain searchable.
        [IO.File]::WriteAllBytes($created,[Text.Encoding]::UTF8.GetBytes("binary`0$token"));Wait-Guide completed
        Move-Item -LiteralPath $created -Destination (Join-Path (Split-Path $ResultsPath) "binary-save-$token.txt")
    }
    Test 'whole-window highlight stays inside capture and is visible' {
        Tool ui_highlight @{target=$fixture.ref;duration_ms=0};$shot=Tool editor_screenshot @{window=$fixture.ref;max_width=0}
        Add-Type -AssemblyName System.Drawing;$bitmap=[Drawing.Bitmap]::new($shot.path)
        try{$orange=$false;foreach($y in 2..6){$c=$bitmap.GetPixel([int]($bitmap.Width/2),$y);if($c.R -gt 230 -and $c.G -gt 150 -and $c.G -lt 220 -and $c.B -lt 130){$orange=$true}};Assert $orange 'whole-window highlight was clipped or absent from capture'}finally{$bitmap.Dispose()}
        Tool ui_highlight @{clear=$true}
    }
    Test 'tutorial presents correct window on automatic, next and previous transitions' {
        try{
            Native test_second_fixture;Act paste $edit.ref @{text='before'}
            Tool ui_guide @{steps=@(@{target=$edit.locator;text='输入切换';until=@{text_contains=@{target=$edit.locator;text='switch'}}},@{target=@{name='guideSecondButton'};text='第二窗口';until='click'},@{target=$edit.locator;text='返回第一窗口';until='manual'})}
            Start-Sleep -Milliseconds 250;Assert ((Tool guide_status).presented_window -eq 'AI Guide native test') 'initial tutorial window'
            Act paste $edit.ref @{text='switch'};Wait-Guide running 1;Start-Sleep -Milliseconds 250
            Assert ((Tool guide_status).presented_window -eq 'AI Guide second window') 'automatic transition did not present next window'
            Assert ((Native test_active_window) -eq 'AI Guide second window') 'second window is not active'
            $second=Tool ui_describe @{target=@{name='guideSecondButton'}};Native test_user_click @{target=$second.ref};Wait-Guide running 2
            Tool guide_control @{action='prev'};Start-Sleep -Milliseconds 250;Assert ((Tool guide_status).presented_window -eq 'AI Guide second window') 'previous did not restore window'
            Tool guide_control @{action='next'};Start-Sleep -Milliseconds 250;Assert ((Tool guide_status).presented_window -eq 'AI Guide native test') 'next did not restore window'
            Tool guide_control @{action='locate'}
        }finally{Tool guide_control @{action='stop'};Native test_close_second}
    }
    Test 'offscreen control auto-scrolls into view with bounded retries' {
        try{Native test_second_fixture;Tool ui_guide @{steps=@(@{target=@{name='guideScrolledEdit'};text='滚动定位';until='manual'})};Start-Sleep -Milliseconds 1200;$s=Tool guide_status;Assert (!$s.waiting -and $s.target.visible -and $s.target.name -eq 'guideScrolledEdit') 'offscreen target did not become visible'}finally{Tool guide_control @{action='stop'};Native test_close_second}
    }
    Test 'tutorial waits for a hidden dialog to open' {
        try{
            Native test_second_fixture @{hidden=$true}
            Tool ui_guide @{steps=@(@{target=@{name='guideSecondButton'};text='等待对话框';until='manual'})}
            Tool guide_control @{action='locate'}
            Start-Sleep -Milliseconds 350
            Assert ((Tool guide_status).waiting) 'the guide did not wait for the hidden dialog'
            Assert ((Tool ui_find @{name='guideSecondWindow';kind='control';visible_only=$true}).total -eq 0) 'locate opened a hidden dialog'
        }finally{Tool guide_control @{action='stop'};Native test_close_second}
    }
    Test 'blocking modal receives the guide and returns to the target after closing' {
        Tool ui_guide @{steps=@(@{target=$edit.locator;text='等待对话框';until='manual'})}
        try{
            $menu=Find-One @{text='Test';kind='menu';exact=$true;under=$fixture.ref};Act click $menu.ref
            $open=Find-One @{text='Open modal';kind='menu';exact=$true};Act click $open.ref
            Start-Sleep -Milliseconds 350;$s=Tool guide_status
            Assert ($s.waiting -and $s.presented_window -eq 'AI Guide modal') 'blocking modal was not presented'
            Native test_close_modal;Start-Sleep -Milliseconds 350;$s=Tool guide_status
            Assert (!$s.waiting -and $s.presented_window -eq 'AI Guide native test') 'target window was not restored'
        }finally{Native test_close_modal;Tool guide_control @{action='stop'}}
    }
    Test 'tutorial exposes file-dialog controls and restores the prior dialog policy' {
        $prior=Native test_dialog_policy
        try{Tool ui_guide @{steps=@(@{target=$button.locator;text='文件对话框检查';until='manual'})};Assert (Native test_dialog_policy) 'tutorial dialog policy';$dialog=Native test_file_dialog;Assert ((Tool ui_find @{name='fileNameEdit';under=$dialog.ref}).total -eq 1) 'file-name control is not exposed to the guide'}
        finally{Native test_close_file_dialog;Tool guide_control @{action='stop'}}
        Assert ((Native test_dialog_policy) -eq $prior) 'dialog policy not restored'
        Tool rehearsal_start;Assert (Native test_dialog_policy) 'rehearsal dialog policy';Tool rehearsal_stop @{rollback=$false};Assert ((Native test_dialog_policy) -eq $prior) 'rehearsal policy not restored'
    }
    Test 'guide waiting, live condition, prev and do_step' {
        Tool ui_guide @{steps=@(@{target=@{name='not-created-yet'};text='等待目标';until='manual'},@{target=$edit.locator;text='输入完成';until=@{text_contains=@{target=$edit.locator;text='done'}}},@{target=$button.locator;text='演示点击';action='click'})}
        Assert ((Tool guide_status).waiting) 'waiting flag';Tool guide_control @{action='next'};Act paste $edit.ref @{text='done'};Wait-Guide running 2
        Tool guide_control @{action='prev'};Assert ((Tool guide_status).index -eq 1) 'prev';Tool guide_control @{action='next'};Tool guide_control @{action='do_step'};Wait-Guide completed
    }
    Test 'key, any, selection, inspector and file-save conditions' {
        $id=Exec 'return Root.Children.First().Id.ToString();';$name=Exec 'return Root.Children.First().Name;';$scene=Exec 'return Root.Name;'
        Exec 'SceneEditorSession.Active.Selection.Clear();EditorUtility.InspectorObject=null;return true;'
        Tool ui_guide @{steps=@(@{target=$edit.locator;text='键盘';until=@{key='Ctrl+J'}},@{target=$edit.locator;text='选择';until=@{any=@(@{selected=$id},@{selected='absent'})}},@{target=$edit.locator;text='检查器';until=@{inspecting=$id}},@{target=$edit.locator;text='写文件';until=@{file_saved=@{ext='txt';contains=$token}}},@{target=$edit.locator;text='已打开的场景';until=@{main_screen=$scene}})}
        Native test_user_key @{target=$edit.ref;keys='Ctrl+J'};Wait-Guide running 1
        Exec ('SceneEditorSession.Active.Selection.Add(Node("'+$id+'"));return true;');Wait-Guide running 2
        Exec ('EditorUtility.InspectorObject=Node("'+$id+'");return true;');Wait-Guide running 3
        [IO.File]::WriteAllText($created,$token);Wait-Guide running 4;Tool guide_control @{action='next'};Wait-Guide completed
        Tool guide_control @{action='restart'};Wait-Guide running 0;Tool guide_control @{action='stop'}
    }
    Test 'rehearsal, scene/file rollback, recipe and restart' {
        [IO.File]::WriteAllText($baseline,'before')
        Tool rehearsal_start @{title='录制测试'}
        Act click $button.locator;Act paste $edit.locator @{text='recorded'}
        [IO.File]::WriteAllText($baseline,'after');[IO.File]::WriteAllText($created,'new')
        Exec 'var s=SceneEditorSession.Active;using(s.UndoScope("AI Guide integration").WithGameObjectCreations().Push()){s.Scene.CreateObject().Name="AI Guide Integration Object";}return true;'
        $stop=Tool rehearsal_stop @{rollback=$true;save_as=$recipe;texts=@('点击按钮','填入示例')}
        Assert ($stop.guide.steps.Count -eq 2) 'recorded steps';Assert ([IO.File]::ReadAllText($baseline) -eq 'before') 'existing file restore';Assert (!(Test-Path $created)) 'new file quarantine';Assert (!(Exec 'return Node("AI Guide Integration Object") != null;')) 'scene undo'
        Assert (Test-Path -LiteralPath "$project/Config/AIGuide/Recipes/$recipe.json") 'recipe must be saved in project config'
        Assert (!(Test-Path -LiteralPath "$project/Libraries/AIGuide/Editor/AIGuide/Recipes/$recipe.json")) 'recipe must not modify Library'
        $recipes=Tool recipe_list;Assert (($recipes|Where-Object name -eq $recipe).source -eq 'project') 'project recipe listing';Assert (@($recipes|Where-Object source -eq builtin).Count -gt 0) 'built-in recipes'
        Tool recipe_play @{name=$recipe;title='回放测试'};Assert ((Tool guide_status).title -eq '回放测试') 'recipe overrides'
        Tool guide_control @{action='next'};Tool guide_control @{action='restart'};Wait-Guide running 0;Tool guide_control @{action='stop'}
    }
    Test 'machine-local dot directories are excluded from rollback' {
        New-Item -ItemType Directory -Force -Path (Split-Path $localOnly)|Out-Null
        [IO.File]::WriteAllText($localOnly,'before');Tool rehearsal_start;[IO.File]::WriteAllText($localOnly,'after');Tool rehearsal_stop @{rollback=$true}
        Assert ([IO.File]::ReadAllText($localOnly) -eq 'after') 'Machine-local data was rolled back'
    }
    if(!$SkipHammer){
      Test 'Hammer live Qt UI discovery' {$hammer=Exec 'return new { Open=Editor.MapEditor.Hammer.Open, File=Editor.MapEditor.Hammer.Open?Editor.MapEditor.Hammer.ActiveMap?.PathName:null };';Assert $hammer.Open 'Open a disposable map in Hammer before running the Hammer test';Assert ((Tool ui_find @{class='Hammer';visible_only=$true}).total -gt 0) 'Hammer Qt UI'}
      Test 'Hammer AI Guide menu opens the tutorial panel' {
        $menu=Find-One @{text='AI Guide';kind='menu';exact=$true;under=@{class='CQHammerMainWnd';kind='control';exact=$true}}
        Act click $menu.ref
        $entry=(Tool ui_find @{text='教程与状态';kind='menu';exact=$true;visible_only=$true}).elements
        Assert ($entry.Count -eq 1) 'Hammer tutorial action'
        Act click $entry[0].ref
        try{Assert ((Tool ui_find @{class='GuidePanel';kind='control';visible_only=$true}).total -eq 1) 'Hammer tutorial panel'}
        finally{Exec 'foreach(var w in Editor.Window.All.Where(w=>w.GetType().FullName=="AIGuide.Sbox.GuidePanel").ToArray())w.Close();return true;'}
      }
      Test 'Hammer viewport remains stable and clickable under guide overlay' {
        $view=(Tool ui_find @{class='CQMapView';exact=$true;visible_only=$true}).elements|Select-Object -First 1;Assert ($null -ne $view) 'Hammer viewport'
        Act focus $view.ref;Tool ui_highlight @{target=$view.ref;text='Hammer 高亮回归';duration_ms=0};Start-Sleep -Milliseconds 200
        $before=Native test_overlay_state;Start-Sleep -Milliseconds 1200;$after=Native test_overlay_state
        Assert ($after.overlays.Count -eq 1 -and $after.overlays[0].input_transparent -and $after.overlays[0].no_activate) 'native viewport overlay input flags'
        Assert ($after.overlays[0].shows -eq $before.overlays[0].shows -and $after.overlays[0].hides -eq $before.overlays[0].hides) 'Hammer overlay toggled visibility'
        Assert ($after.focus -eq $before.focus) 'Hammer focus changed during timer'
        Act click $view.ref;$log=(Tool guide_status).log
        Assert (@($log|Where-Object {$_.event -eq 'click' -and $_.target.class -eq 'CQMapView'}).Count -gt 0) 'Hammer did not receive the click'
        Tool ui_highlight @{clear=$true}
      }
    }
    Test 'closed unsaved scene restored from memory checkpoint' {
        $goId=Exec 'var s=SceneEditorSession.CreateDefault();s.Scene.Name="AI Guide Closed Scene Test";s.MakeActive();var go=s.Scene.CreateObject();go.Name="AI Guide Closed Scene Object";return go.Id.ToString();'
        Tool rehearsal_start @{title='关闭场景回退'};Exec 'SceneEditorSession.Active.Destroy();return true;';Tool rehearsal_stop @{rollback=$true}
        Assert ((Exec ('return SceneEditorSession.Active.Scene.Directory.FindByGuid(new Guid("'+$goId+'")) is GameObject;'))) 'closed scene snapshot'
        Assert ((Exec 'return Root.Name;') -eq 'AI Guide Closed Scene Test') 'scene title restore'
        Exec 'SceneEditorSession.Active.Destroy();SceneEditorSession.All.First().MakeActive();return true;'
    }
    Test 'file bridge JSON-RPC' {$bridge=Join-Path $project '.ai_guide_bridge';New-Item -ItemType Directory -Force "$bridge/in"|Out-Null;$id=[Guid]::NewGuid().ToString('N');[IO.File]::WriteAllText("$bridge/in/$id.json",'{"jsonrpc":"2.0","id":1,"method":"ping"}');$until=[DateTime]::UtcNow.AddSeconds(5);while(!(Test-Path "$bridge/out/$id.json") -and [DateTime]::UtcNow -lt $until){Start-Sleep -Milliseconds 100};Assert (Test-Path "$bridge/out/$id.json") 'file bridge response'}
    Test 'HTTP Host/Origin and method validation' {
        $body='{"jsonrpc":"2.0","id":1,"method":"ping"}'
        Assert ((Invoke-WebRequest $BaseUrl -Method Post -Headers @{Host='untrusted.invalid'} -Body $body -SkipHttpErrorCheck).StatusCode -eq 403) 'Host check'
        Assert ((Invoke-WebRequest $BaseUrl -Method Post -Headers @{Origin='https://untrusted.invalid'} -Body $body -SkipHttpErrorCheck).StatusCode -eq 403) 'Origin check'
        Assert ((Invoke-WebRequest $BaseUrl -Method Get -SkipHttpErrorCheck).StatusCode -eq 405) 'GET check'
    }
    Test 'modal, overlay and weak-reference lifetime (8 cycles)' {
        for($i=0;$i -lt 8;$i++){
            $menu=Find-One @{text='Test';kind='menu';exact=$true};Act click $menu.ref;$open=Find-One @{text='Open modal';kind='menu';exact=$true};Act click $open.ref
            $input=Find-One @{name='modalText';kind='control'};Tool ui_highlight @{targets=@($input.ref);text="Modal $i";duration_ms=0};$close=Find-One @{name='modalClose';kind='control'};Act click $close.ref
            Tool ui_highlight @{targets=@($button.ref);text="Restored $i";duration_ms=0};$image=Tool editor_screenshot @{window=$fixture.ref};Assert (Test-Path $image.path) 'repeat capture'
        }
        Tool ui_highlight @{clear=$true}
    }
    Test 'persistent locator after widget recreation and expired ref rejection' {
        Native test_close;Start-Sleep -Milliseconds 150;$script:fixture=Native test_fixture
        Assert ((Tool ui_describe @{target=$button.locator}).name -eq 'guideIncrement') 'persistent locator'
        $rejected=$false;try{Tool ui_describe @{target=$button.ref}}catch{$rejected=$true};Assert $rejected 'expired ref must reject'
    }
    Test 'plugin reload and service recovery' {Tool ai_guide_reload;Start-Sleep -Milliseconds 800;Assert ((Rpc ping) -is [object]) 'reload ping';Assert ((Tool editor_state).native.native_ui) 'reload native bridge'}
}finally{
    try{Tool guide_control @{action='stop'}|Out-Null;if(!$KeepFixture){Native test_close|Out-Null}}catch{}
    foreach($path in @($baseline,$created,$localOnly,(Join-Path $project "Config/AIGuide/Recipes/$recipe.json"))){if(Test-Path $path){$dest=Join-Path (Split-Path $ResultsPath) ([IO.Path]::GetFileName($path));Move-Item -LiteralPath $path -Destination $dest -Force}}
    $results|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $ResultsPath -Encoding utf8
}
$failed=@($results|Where-Object {!$_.passed});Write-Output "$($results.Count-$failed.Count)/$($results.Count) passed; $ResultsPath"
if($failed.Count){exit 1}
