// 既存の入力要素を移動し、イベント・保存設定を一つに保つ。
export function installWorkspace(context) {
  const $=id=>document.getElementById(id), q=selector=>document.querySelector(selector);
  const element=(tag,attrs={},text)=>{const e=document.createElement(tag);for(const[k,v]of Object.entries(attrs))e.setAttribute(k,v);if(text)e.textContent=text;return e;};
  const button=(id,text,action)=>{const b=element('button',{id},text);b.onclick=action;return b;};
  const dialog=(id,title)=>{const d=element('dialog',{id}),head=element('div',{class:'dialog-head'});head.append(element('h2',{},title),button(id+'Close','閉じる',()=>d.close()));d.append(head);document.body.append(d);return d;};
  const settings=dialog('settingsDialog','設定'), range=dialog('rangeDialog','録音・確認する区間');
  range.append(q('.range-tools'));q('.range-tools').open=true;
  // よく使う区間の操作を上に置く。今の位置から録り直す(A=今・B=曲の終わり・置き換え)と、区間の解除を1回で。
  // 確かめるでは、録り直しの代わりに「今の位置からくり返し聴く」(2小節、BPM未設定なら5秒)。
  const quick=element('div',{class:'range-quick'});quick.append(button('redoFromHere','今の位置から最後まで録り直す',context.redoFromHere),button('loopHere','今の位置からくり返し聴く',context.loopFromHere),button('clearRange','区間を解除',context.clearRange));range.querySelector('.dialog-head').after(quick);
  // 確かめるにも区間の入口を置く(「区間」は録るにしかなく、ループは確認のときしか使えなかった)。
  const previewTools=element('div',{class:'preview-tools'});previewTools.append(button('previewRangeButton','区間・ループ',()=>range.showModal()));
  const groups=[['曲設定',['bpm','difficulty','beatZero','offset','level']],['打ち方',['snapMode','snap','snapWindowMs','flickEnabled','flickSensitivity','positionGrid','longCount','direction']],['この端末',['latency','songVolume','feedbackVolume']],['聴き方',['speed','countIn','clickSound','metronome']]];
  // BPMをたたいて測るには曲が要るので、測る道具の横で曲を流せるようにする(録音はしない)。
  const tempoListen=button('tempoListen','曲を流す',context.listenForTempo);q('.tempo-tools').prepend(tempoListen);
  for(const [title,ids] of groups){const details=element('details',{class:'settings-group'});details.open=title==='曲設定';details.append(element('summary',{},title));const fields=element('div',{class:'fields'});for(const id of ids)fields.append($(id).closest('label'));details.append(fields);if(title==='曲設定'){details.append(q('.tempo-tools'),q('.refinement-tools'));}if(title==='打ち方'){details.append($('snapHint'));const label=element('label',{},'ロングになる長押し（ms）'),input=element('input',{id:'longThresholdMs',type:'number',min:'150',max:'1000',step:'10',inputmode:'numeric',value:'300'});label.append(input);fields.append(label);}if(title==='この端末')details.append(element('p',{class:'hint'},'入力補正を＋にすると新しい入力を早めます。スピーカー・有線・Bluetoothで遅れが異なります。音源上の時刻を聴いて調整してください。'));if(title==='聴き方')details.append(element('p',{class:'hint'},'速度を下げると音程も下がります。BPM未設定では拍の音・カウントは使いません。'));settings.append(details);}
  // ヘッダーは曲名と設定、ライブラリに絞る。
  const top=q('.topbar');q('.brand').remove();const heading=button('songSettings','',()=>settings.showModal());heading.setAttribute('aria-label','曲の設定');heading.append($('difficultyLabel'),$('songTitle'));top.prepend(heading);top.append(button('settingsButton','設定',()=>settings.showModal()));
  const save=$('saveStatus');top.after(save);save.setAttribute('role','status');
  const audioLabel=$('audioFile').closest('label');q('.song-strip').remove();
  // 前回の下書きがあれば、最初の画面から1回で続きを開ける(app.js が名前と開き方を入れる)。
  const resume=element('button',{id:'resumeProject',class:'primary resume-project',hidden:''});
  const welcome=element('section',{id:'welcome',class:'welcome'});welcome.append(element('h1',{},'曲を聴いて、譜面を作ろう'),element('p',{},'音源と下書きはこの端末に保存します。まず曲を開くか、「曲・保存」から再開してください。'),resume,audioLabel);top.parentElement.insertBefore(welcome,q('.workspace'));
  const workspace=q('.workspace'),performance=q('.performance'),editing=q('.editing');workspace.className='workspace mobile-workspace';
  const tabs={};for(const name of ['record','edit','preview','export']){tabs[name]=element('section',{id:`tab-${name}`,class:'workspace-tab',role:'tabpanel','aria-labelledby':`nav-${name}`});workspace.append(tabs[name]);}tabs.record.append(performance);tabs.edit.append($('timelineDialog'),$('noteDialog'));tabs.preview.append($('reportDialog'),previewTools);tabs.export.append(q('.export-panel'));
  q('.surface-head').hidden=true;q('.notes-panel').hidden=true;editing.hidden=true;
  const tools=element('div',{class:'record-tools'});tools.append(button('rangeButton','区間',()=>range.showModal()),$('noteCount'));q('.stage-wrap').before(tools);
  const warning=element('output',{id:'recordWarning',class:'record-warning','aria-live':'polite'});q('.pad-toolbar').before(warning);q('.pad-toolbar').append($('rangeButton'));
  $('undoButton').textContent='元に戻す';$('redoButton').textContent='やり直し';$('retryTake').textContent='録り直す';q('.take-actions').append(button('listenTake','▶ 聴く',context.listenTake));
  const padHint=$('padHint');padHint.hidden=true;$('inputFeedback').textContent='タップ・フリック・長押しで入力';
  for(const id of ['noteDialog','timelineDialog','reportDialog']){$(id).classList.add('inline-panel');$(id).querySelector('.dialog-head').hidden=true;}
  // 削除は見出しの横に置く(打ち間違いを続けて消すとき、下までスクロールしない)。
  const noteHead=element('div',{class:'inline-note-head'});noteHead.append(element('strong',{},'選択ノーツ · 変更は即保存'),$('deleteNote'),button('closeNote','閉じる',()=>$('noteDialog').close()));$('noteDialog').prepend(noteHead);
  $('noteEditorHost').append($('noteEditor'));
  const directionGrid=element('div',{id:'directionGrid',class:'direction-grid',role:'group','aria-label':'ノーツの矢印'});
  for(const [value,text] of [['upleft','↖'],['up','↑'],['upright','↗'],['left','←'],['none','自由'],['right','→'],['downleft','↙'],['down','↓'],['downright','↘']]){const b=button('direction-'+value,text,()=>{context.edit('noteDirection',value);if($('autoNext').checked)$('nextNote').click();});b.dataset.direction=value;b.setAttribute('aria-label',value==='none'?'矢印なし':`${text}の矢印`);directionGrid.append(b);}
  const directions=element('div',{class:'direction-tools'});directions.append(directionGrid);const auto=element('label',{class:'check'});auto.append(element('input',{type:'checkbox',id:'autoNext'}),document.createTextNode('矢印の変更後に次へ'));directions.append(auto,button('listenNote','選択ノーツを聴く',context.listenNote));$('noteEditorHost').before(directions);
  $('noteDirection').closest('label').hidden=true;$('updateNote').hidden=true;
  const detail=element('details',{class:'note-details'});detail.append(element('summary',{},'位置・ロングの長さ'));const fields=element('div',{class:'fields'});for(const id of ['noteX','noteY','noteLength'])fields.append($(id).closest('label'));detail.append($('positionPad'),fields);$('noteEditor').append(detail);
  $('noteDialog').querySelector(':scope > .hint').hidden=true;
  const map=$('timelineDialog');map.open=true;map.querySelector(':scope > .hint').textContent='横は実際のX位置、上ほど先の時刻。四角を選んで編集、空白をドラッグして移動。重なりは下の一覧から選べます。';
  $('chartMap').setAttribute('aria-label','縦の譜面タイムライン。X位置・色・方向・ロングを表示。下の一覧から全ノーツを選択できます。');$('mapZoom').value='5';
  const notes=element('details',{class:'map-note-details'});notes.append(element('summary',{},'表示区間のノーツ一覧'),$('mapNotes'));map.append(notes);
  for(const id of ['mapBatch','batchButton'])$(id).hidden=true;
  $('mapRefine').textContent='まとめて直す';$('refineButton').textContent='まとめて直す';
  map.querySelector('.row').append(button('placeNote','＋ 置く',context.placeNote));
  // 直す画面でも、元に戻す・やり直しを並べる(やり直しのために「録る」へ移らなくてよい)。右寄せなので並びは DOM の逆。
  $('mapStats').after(button('editRedo','やり直し',()=>$('redoButton').click()),button('editUndo','元に戻す',()=>$('undoButton').click()));
  $('help').innerHTML='<div class="dialog-head"><h2>曲を聴いて、譜面を作る</h2><button id="closeHelp">閉じる</button></div><ol><li>音源を開きます。新規曲のBPMは未設定で、打った時刻をそのまま記録します。</li><li>「録る」で録音開始。左は青、右は赤。タップ・フリック・長押しを使えます。「元に戻す」は1回の録音をまとめて取り消します。「録り直す」は直前の録音を置き換えます。</li><li>「設定」でBPM・拍の始まりを確認してから、必要なら「拍への補正」を有効にします。BPM変更で録音済みノーツは動きません。</li><li>「直す」の縦タイムラインでノーツを選びます。値は入力を確定すると即保存。「次へ」に進む前の適用操作は不要です。空白を触っても追加されず、「＋ 置く」で追加できます。</li><li>「まとめて直す」でA–B・色・操作を指定し、変更件数を確認して適用します。編集画面にも「元に戻す」があります。</li><li>「確かめる」で試聴し、診断候補を確認します。診断は本編の採点を再現しません。金はどちらの手でも切れます。</li><li>「渡す」でJSONを書き出すか共有し、表示される手順に沿ってUnityへ渡します。音源入り.saberバックアップで別端末へ移せます。</li></ol><p>「区間」の置き換えは、入力した場合だけ、再生した範囲の既存ノーツを消します。途中停止より先のノーツは残ります。録音中は停止するまで別画面へ移れません。</p><p>iPhoneは共有メニューの「ホーム画面に追加」、Androidはブラウザのインストールから使えます。「オフラインで使用できます」の表示後は保存済み音源で作業できます。</p><p>端末のデータを消す前にバックアップを保存してください。別タブと編集が競合した場合は上書きを止めます。手元の譜面を書き出してから再読み込みしてください。</p>';$('closeHelp').onclick=()=>$('help').close();
  // 曲・保存は続きの下書きを開く場所。操作は2列に詰めて、下書きの一覧が最初の画面に入るようにする。曲名の変更は一覧の下へ。
  const library=$('library'),actions=element('div',{class:'library-actions'}),oldRow=library.querySelector('.row');
  actions.append($('libraryAudioFile').closest('label'),$('chartFile').closest('label'),$('duplicateButton'),$('libraryBackupFile').closest('label'));
  oldRow.remove();library.querySelector(':scope > .hint').after(actions);$('projectList').after($('projectNameLabel'));
  $('chartFile').before(element('small',{id:'importTarget',class:'import-target'}));$('chartFile').closest('label').classList.add('import-label');
  $('backupFile').closest('label').hidden=true;
  const unity=element('section',{class:'unity-guide'});unity.append(element('h2',{},'Unityへ渡す'));const list=element('ol');for(const text of ['PCへ譜面JSONと音源を渡します。既存曲への上書き前にファイルをバックアップしてください。','Unityエディター内の未保存編集を保存または破棄してから、Assets/StreamingAssets/Songs/<曲ID>/ に chart_easy.json・chart_normal.json・chart_hard.json と音源（audio.mp3・audio.ogg・audio.wav のどれか1つ）を置きます。','譜面エディターの「読込先」で該当する難易度を選び「読込」。曲情報はUnity側で設定します。','NORMALをUnityで保存すると互換用の chart.json も更新されます。スマホからのファイルを配置するだけで、開いている編集内容が自動更新されるわけではありません。'])list.append(element('li',{},text));unity.append(list,element('p',{class:'hint'},'金ノーツはどちらの手でも切れます。確かめる画面の診断は目安で、本編の採点や難易度判定を再現するものではありません。'));tabs.export.append(unity);
  q('.export-panel > .hint').textContent='chart_easy / normal / hard.json の名前で保存します。音源がM4Aなどの場合はWAV保存してPCへ渡してください。';
  $('exportButton').after(button('shareChart','共有する',context.shareChart));q('.export-panel').prepend(element('p',{id:'exportWarnings',class:'change-preview',role:'status',hidden:''}));
  // 実際に保存されるファイル名と、ゲームの曲フォルダーでの名前(app.js の exportNames)。
  $('exportButton').closest('.row').after(element('p',{id:'exportNames',class:'export-names'}));
  const nav=element('nav',{class:'bottom-tabs',role:'tablist','aria-label':'作業画面'});let active=null;
  for(const [name,label] of [['record','録る'],['edit','直す'],['preview','確かめる'],['export','渡す']]){const b=button(`nav-${name}`,label,()=>activate(name));b.setAttribute('role','tab');b.setAttribute('aria-controls',tabs[name].id);nav.append(b);}
  workspace.after(nav);$('library').append(q('footer'));$('offlineStatus').before(element('strong',{id:'appBrand'},'Saber Tap Studio'));
  const shared=[q('.stage-wrap'),q('.transport'),q('.timeline-wrap')];
  // タブごとにスクロール位置を覚える(前のタブで下へ送った位置のまま、別のタブの途中が開かないように)。
  const scrolls={};
  function activate(name){
    if(active===name)return true;if(context.locked())return false;
    context.stop();if(active)scrolls[active]=window.scrollY;active=name;
    for(const[key,panel]of Object.entries(tabs)){panel.hidden=key!==name;$('nav-'+key).setAttribute('aria-selected',key===name);$('nav-'+key).tabIndex=key===name?0:-1;}
    if(name==='preview')$('reportDialog').before(...shared,previewTools);else tools.after(...shared);
    if(name==='edit')$('timelineDialog').open=true;
    if(name==='preview')$('reportDialog').open=true;
    document.body.dataset.tab=name;context.onTab(name);window.scrollTo(0,scrolls[name]||0);return true;
  }
  nav.addEventListener('keydown',event=>{if(!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;event.preventDefault();const names=Object.keys(tabs),index=names.indexOf(active),next=event.key==='Home'?0:event.key==='End'?3:(index+(event.key==='ArrowLeft'?3:1))%4;if(activate(names[next]))$('nav-'+names[next]).focus();});
  // 数字キーボードに負号がなくても方向を切り替えられる。
  for(const[id,negative,positive]of [['latency','遅く記録','早く記録'],['offset','早く','遅く'],['beatZero','前へ','後へ'],['noteX','左','右'],['noteY','下','上'],['refineShift','早く','遅く'],['refineX','左','右'],['refineY','下','上']]){
    const input=$(id),row=element('div',{class:'sign-controls'});for(const[sign,label]of [[-1,negative],[1,positive]])row.append(button(id+(sign<0?'Negative':'Positive'),label,()=>{input.value=sign*Math.abs(Number(input.value)||0);input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));}));input.after(row);
  }
  return {activate,get active(){return active;},refresh(state){
    const locked=context.locked();document.body.classList.toggle('is-recording',locked);document.body.classList.toggle('has-project',!!state.project);welcome.hidden=!!state.project;workspace.hidden=!state.project;nav.hidden=!state.project;
    for(const b of nav.querySelectorAll('button'))b.disabled=locked;
    for(const id of ['songSettings','settingsButton','libraryButton','rangeButton'])$(id).disabled=locked||state.loading;
    // 設定は下書きごとに保存するので、曲を開くまでは押せない(開く前に変えても、曲を開くと消えていた)。
    for(const id of ['songSettings','settingsButton'])$(id).disabled||=!state.project;
    // 譜面JSONは、今開いている曲の新しい下書きになる。どの曲へ入るかを「譜面を開く」に書いておく。
    $('importTarget').textContent=state.project?`→「${state.project.name}」へ`:'先に音源を開く';
    $('listenTake').disabled=!state.lastTake||state.playing;$('editUndo').disabled=$('undoButton').disabled;$('editRedo').disabled=$('redoButton').disabled;$('shareChart').disabled=!state.project||state.loading;
    $('recordWarning').textContent=locked?'● 録音中 · 停止してから画面を切り替えられます':'区間指定なし · 入力を追加';
    if($('rangeEnabled').checked)$('recordWarning').textContent=`${$('replaceRange').checked?'⚠ 入力した区間を置き換え':'A–Bに追加'} · ${Number($('rangeStart').value).toFixed(2)}〜${Number($('rangeEnd').value).toFixed(2)}秒`;
    $('recordWarning').classList.toggle('replacing',$('rangeEnabled').checked&&$('replaceRange').checked);
    $('rangeButton').textContent=$('rangeEnabled').checked?'区間 ON':'区間';$('rangeButton').classList.toggle('range-on',$('rangeEnabled').checked);
    $('clearRange').hidden=!$('rangeEnabled').checked;$('redoFromHere').disabled=locked||state.loading||!state.project||state.playing;
    // 区間の画面は、録るでは録り直し、確かめるではくり返し聴く方を出す(区間内を録り直すは録る専用)。
    const previewing=active==='preview';$('redoFromHere').hidden=previewing;$('loopHere').hidden=!previewing;$('loopHere').disabled=state.loading||!state.project||state.playing;
    $('replaceRange').closest('label').hidden=previewing;
    $('previewRangeButton').textContent=$('rangeEnabled').checked?'区間・ループ ON':'区間・ループ';$('previewRangeButton').classList.toggle('range-on',$('rangeEnabled').checked);$('previewRangeButton').disabled=state.loading||!state.project||state.playing;
  },direction(value){for(const b of directionGrid.children){b.setAttribute('aria-pressed',b.dataset.direction===value);b.classList.toggle('active',b.dataset.direction===value);}}};
}
