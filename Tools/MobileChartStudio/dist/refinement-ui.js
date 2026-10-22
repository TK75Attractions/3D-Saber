import { transformRange } from './editing.js';
import { refineChart, chartReport, noteSeek, beatSeek, chartWithBeatOrigin, safeBookmarks, filterProjects } from './refinement.js';
import { formatTime } from './core.js';
import { deleteProject, getAudio, listProjects } from './storage.js';

export function installRefinementUI(context) {
  let issueLimit = 200, limitFor = null;
  const {getState, mutate, stop, seek, persist, run, toast, selectNote, downloadBlob} = context;
  const $ = id => document.getElementById(id);
  let pending = null;
  const operationNames = {alignTime:'同時押しの時刻統一',copy:'区間コピー',mirror:'左右反転',shift:'時刻移動', height:'同時押しの高さ統一', counts:'同時ロングの回数統一', symmetry:'左右位置の対称化', snap:'近い拍へ補正', ends:'ロング終端を拍へ整列', delete:'区間削除', dedupe:'完全重複の除去', color:'色の変更', direction:'矢印の変更', longCount:'ロング回数の変更', position:'位置の移動', vertical:'上下反転'};

  function clearPreview() { pending = null; $('refineApply').disabled = true; $('refineApply').textContent = 'この変更を適用'; $('refineTargetCount').textContent=''; $('refinePreview').textContent = '「変更内容を確認」で対象と変更件数を確認できます。'; }
  // 曲全体のときは A・B の欄を隠して「曲全体が対象」と書く(A・Bを書き換えても範囲が曲全体のまま、という取り違えを防ぐ)。
  function showScope() {
    const whole = $('refineScope').value !== 'range';
    document.querySelector('.refine-range').hidden = whole; $('refineScopeNote').textContent = whole ? '曲全体のノーツが対象です。区間だけにするときは「A–Bの区間」を選びます。' : 'A以上・B未満に始まるノーツが対象です。';
  }
  function showRefinement() {
    if (!getState().project) return; stop(); $('refineStart').value=$('rangeStart').value;$('refineEnd').value=$('rangeEnd').value;clearPreview(); showScope(); $('refineDialog').showModal();
  }
  document.querySelector('.refine-range').before(Object.assign(document.createElement('p'), {id:'refineScopeNote', className:'hint'}));
  $('refineScope').addEventListener('change', showScope);
  function preview() {
    stop(); const {chart, audio} = getState(), scope = {color:$('refineColor').value};
    if ($('refineScope').value === 'range') {
      const start = Number($('refineStart').value), end = Number($('refineEnd').value);
      if ($('refineStart').value==='' || $('refineEnd').value==='' || !Number.isFinite(start) || !Number.isFinite(end) || start < 0 || end <= start || end > audio.duration + .001) throw new Error('開始A・終了Bを音源内で指定してください。');
      Object.assign(scope, {start, end});
    }
    const kind = $('refineKind').value;
    if(['snap','ends'].includes(kind)&&getState().bpmConfirmed===false)throw new Error('先に曲設定でBPMを確定してください。未設定の拍にはそろえません。');
    const required={shift:['refineShift'],copy:['refineDestination'],position:['refineX','refineY'],longCount:['refineLongCount']}[kind]||[];
    if(required.some(id=>$(id).value===''))throw new Error('変更量を入力してください。空欄のまま適用できません。');
    const operation = {kind, ms:Number($('refineShift').value), toleranceMs:Number($('chordTolerance').value), step:Number($('refineGrid').value), windowMs:Number($('refineWindow').value), color:$('refineNewColor').value, direction:$('refineDirection').value, count:Number($('refineLongCount').value), x:Number($('refineX').value), y:Number($('refineY').value)};
    let result;
    if(kind==='copy'||kind==='mirror'){const range={start:scope.start??0,end:scope.end??audio.duration,color:scope.color};result=transformRange(chart,range,{kind,value:Number($('refineDestination').value),swapColors:$('refineSwap').checked},audio.duration);result.targetCount=result.count;}else result=refineChart(chart, scope, operation, audio.duration);
    $('refineTargetCount').textContent=`対象 ${result.targetCount} 個 · ${scope.start===undefined?'曲全体':scope.start+'〜'+scope.end+'秒'}`;
    pending = {before:JSON.stringify(chart), ...result, kind};
    $('refinePreview').textContent = `${operationNames[kind]}：対象 ${result.targetCount} 個のうち ${result.count} 個を${kind === 'delete' || kind === 'dedupe' ? '削除' : '変更'}します。${kind === 'shift' ? ` ${operation.ms}ms（${operation.ms < 0 ? '早く' : '遅く'}）` : ''}「元に戻す」で取り消せます。`;
    $('refineApply').disabled = result.count === 0;
    // 押す前に何が起きるか分かるよう、適用ボタンにも操作と件数を書く。
    if (result.count) $('refineApply').textContent = `${operationNames[kind]}を ${result.count} 個に適用`;
  }
  $('refineButton').onclick = showRefinement;
  $('mapRefine').onclick = showRefinement;
  $('refineCheck').addEventListener('click', run(preview));
  $('refineApply').addEventListener('click', run(() => {
    if (!pending) return;
    if (JSON.stringify(getState().chart) !== pending.before) { clearPreview(); throw new Error('譜面が変更されたため、もう一度確認してください。'); }
    const {chart, count, kind} = pending; mutate(chart); clearPreview(); $('refineDialog').close(); toast(`${operationNames[kind]}：${count} 個を変更しました。「元に戻す」で取り消せます。`);
  }));
  for (const input of $('refineDialog').querySelectorAll('input,select')) input.addEventListener('input', clearPreview);
  $('refineKind').addEventListener('change', () => {
    for (const group of document.querySelectorAll('[data-refine-kinds]')) group.hidden = !group.dataset.refineKinds.split(' ').includes($('refineKind').value);
  });
  $('refineDirection').innerHTML = $('direction').innerHTML;

  // keepPlaying: 開いている「確かめる」を、譜面や設定が変わったときに描き直す(再生は止めない)。
  function reportChart({keepPlaying = false} = {}) {
    if (!getState().project) return; if (!keepPlaying) stop();
    const {chart, audio, difficulty,bpmConfirmed,project} = getState(); if (project.id !== limitFor) { issueLimit = 200; limitFor = project.id; }
    const report = chartReport(chart, audio.duration, bpmConfirmed===false?undefined:difficulty, issueLimit);
    // 山が音源の前(負の時刻)から始まるときは「0:00.000から」と丸めず符号を付ける(統計は音源外のノーツも数えたまま)。
    const peakMs = Math.round(report.peakStart * 1000), peakTime = Number.isFinite(peakMs) && peakMs < 0 ? '-' + formatTime(-peakMs / 1000, true) : formatTime(report.peakStart,true);
    $('reportStats').textContent = `タップ ${report.counts.tap} / フリック ${report.counts.flick} / ロング ${report.counts.long}\n青 ${report.counts.blue} / 赤 ${report.counts.red} / 金 ${report.counts.gold} / その他 ${report.counts.default}\n平均 ${report.average.toFixed(2)} 個/秒 · 最大2秒密度 ${report.peakNps.toFixed(2)} 個/秒（${peakTime}から）`;
    $('reportDensity').replaceChildren(); const maximum = Math.max(1, ...report.buckets.map(bucket => bucket.count));
    for (const bucket of report.buckets) {
      const button = document.createElement('button'); button.className = 'density-bucket';
      button.style.setProperty('--density', `${bucket.count / maximum * 100}%`);
      button.textContent = `${formatTime(bucket.start)}〜 ${bucket.count}個`;
      button.onclick = () => { seek(bucket.start); };
      $('reportDensity').append(button);
    }
    $('reportIssueCount').textContent = (report.issueCount ? `確認候補 ${report.issueCount} 件（自動修正はしません）` : '現在の検査では見直し候補は見つかりませんでした。')+(bpmConfirmed===false?' BPM未設定のため難易度の拍間隔は診断しません。':'');
    $('reportIssues').replaceChildren();
    // 候補を押すと「点検モード」で直すへ。前へ/次へで前後の候補を順に回れる。
    const items = report.issues.map(issue => ({id: issue.id, text: issue.text}));
    report.issues.forEach((issue, index) => {
      const button = document.createElement('button'); button.textContent = `${formatTime(issue.time,true)} · ${issue.text}`;
      button.onclick = () => { selectNote(issue.id, {review: {items, index}}); }; $('reportIssues').append(button);
    });
    // 多いときは重い順(音源の外・重複 → 手の重なり → 間隔 → 難易度の目安)に出し、続きは押せば200件ずつ出す(意図した配置を直さなくても残りへ行ける)。
    if (report.issueCount > report.issues.length) { const rest = report.issueCount - report.issues.length, text = document.createElement('p'), more = document.createElement('button'); text.className = 'hint'; text.textContent = `全 ${report.issueCount} 件のうち ${report.issues.length} 件を、重い順(音源の外・重複 → 手の重なり → 間隔 → 難易度の目安)に出しています。`; more.textContent = `続きの ${Math.min(200, rest)} 件を表示`; more.onclick = () => { issueLimit += 200; reportChart({keepPlaying: true}); }; $('reportIssues').append(text, more); }
    // 候補の一覧は下のほうにあるので、統計のすぐ下に件数と近道を出す。
    let jump = $('reportJump'); if (!jump) { jump = document.createElement('button'); jump.id = 'reportJump'; jump.className = 'subtle report-jump'; jump.onclick = () => $('reportIssueCount').scrollIntoView({block: 'start'}); $('reportStats').after(jump); }
    jump.textContent = `見直す候補 ${report.issueCount} 件 ↓`; jump.hidden = !report.issueCount;
    }
  $('reportButton').addEventListener('click',()=>context.openPreview());
  for (const [id, direction] of [['jumpPreviousNote',-1],['jumpNextNote',1]]) $(id).onclick = () => { const {chart,audio} = getState(); seek(noteSeek(chart,audio.time(),direction,audio.duration)); };
  for (const [id, direction] of [['jumpPreviousBeat',-1],['jumpNextBeat',1]]) $(id).onclick = () => { const {chart,audio} = getState(); seek(beatSeek(chart,audio.time(),direction,audio.duration)); };
  $('setBeatOrigin').onclick = () => {
    if (!getState().project) return; stop(); const {chart,audio} = getState();
    mutate(chartWithBeatOrigin(chart,audio.time())); context.fillChartFields(); toast('今の位置を拍の始まりにしました。ノーツの時刻は維持します。');
  };
  function renderBookmarks() {
    const {project,audio} = getState(); $('bookmarkList').replaceChildren();
    for (const [index, item] of safeBookmarks(project?.bookmarks, audio.duration).entries()) {
      const row = document.createElement('div'); row.className = 'bookmark-row';
      const button = document.createElement('button'); button.textContent = `${formatTime(item.time,true)} · ${item.name}`; button.onclick = () => seek(item.time);
      const remove = document.createElement('button'); remove.textContent = '削除'; remove.className = 'subtle'; remove.setAttribute('aria-label', `${item.name}の目印を削除`);
      remove.addEventListener('click', run(async () => { project.bookmarks = safeBookmarks(project.bookmarks,audio.duration).filter((_,i) => i !== index); await persist(); renderBookmarks(); }));
      row.append(button,remove); $('bookmarkList').append(row);
    }
  }
  $('addBookmark').addEventListener('click', run(async () => {
    const {project,audio} = getState(); if (!project) return;
    const bookmarks = safeBookmarks(project.bookmarks,audio.duration); if (bookmarks.length >= 100) throw new Error('目印は100個までです。');
    bookmarks.push({name:$('bookmarkName').value.trim() || `目印 ${bookmarks.length+1}`,time:audio.time()}); project.bookmarks = safeBookmarks(bookmarks,audio.duration);
    await persist(); $('bookmarkName').value = ''; renderBookmarks(); toast('今の位置に目印を保存しました。');
  }));
  $('exportOriginalAudio').addEventListener('click', run(async () => {
    const {project} = getState(); if (!project) return;
    // 別のタブで曲ごと消されて保存した音源が無いときも、開いている音源はWAVで書き出せる(app.js の downloadBackup と同じ案内)。
    const source = await getAudio(project.audioId); if (!source?.blob) throw new Error('元の音源が別のタブで削除されています。「音源をWAV保存」で、開いている音源を保存してください。');
    downloadBlob(source.blob, source.name.replace(/[<>:"/\\|?*\u0000-\u001f]/g,'_') || 'audio'); toast('元の音源を変換せず保存しました。');
  }));
  $('projectSearch').addEventListener('input', () => {
    for (const row of $('projectList').querySelectorAll('[data-project-name]')) row.hidden = !filterProjects([{name:row.dataset.projectName,difficulty:row.dataset.projectDifficulty}],$('projectSearch').value).length;
    const visible = [...$('projectList').querySelectorAll('[data-project-name]')].filter(row => row.dataset.projectName !== undefined && !row.hidden).length;
    for(const group of $('projectList').querySelectorAll('.project-group'))group.hidden=![...group.querySelectorAll('[data-project-name]')].some(row=>!row.hidden);
    $('projectSearchStatus').textContent = `${visible} 件の下書き`;
  });
  async function removeProject(item) {
    // 同じ名前の下書きが並んでも取り違えないよう、ノーツ数・更新日時・編集中かを書く。最後の下書きなら音源も消えることも書く。
    const others = (await listProjects()).filter(p => p.audioId === item.audioId && p.id !== item.id).length, editing = getState().project?.id === item.id;
    const detail = `${item.chart?.notes?.length || 0} notes・最終更新 ${new Date(item.updated).toLocaleString('ja-JP')}${editing ? '・編集中' : ''}`;
    const audioNote = others ? `この曲のほかの下書き ${others} 件が同じ音源を使うので、音源は残ります。` : 'この曲の最後の下書きなので、音源もこの端末から削除されます。';
    if (!window.confirm(`「${item.name}」の${(item.difficulty||'normal').toUpperCase()}下書き(${detail})を削除します。\n${audioNote}\n必要なら先にバックアップを保存してください。削除しますか？`)) return;
    stop(); await persist(); await deleteProject(item.id);
    if (getState().project?.id === item.id) context.clearProject();
    await context.projectsChanged?.();
    await context.showLibrary(); toast(others ? '下書きを削除しました。同じ曲のほかの下書きが使う音源は残しています。' : '下書きと、その曲の音源を削除しました。');
  }
  function decorateProject(row,item) {
    row.dataset.projectName = item.name; row.dataset.projectDifficulty = item.difficulty || 'normal';
    const button = document.createElement('button'); button.className = 'subtle danger'; button.textContent = '削除'; button.setAttribute('aria-label',`${item.name} ${(item.difficulty||'normal').toUpperCase()}を削除`); button.addEventListener('click',run(() => removeProject(item))); row.append(button);
  }
  return {
    decorateProject,report:reportChart,
    refreshLibrary() { $('projectSearch').dispatchEvent(new Event('input')); },
    refresh() {
      const {project,loading,audio,mode} = getState();
      for (const id of ['refineButton','mapRefine','reportButton','exportOriginalAudio','addBookmark','setBeatOrigin','jumpPreviousNote','jumpNextNote','jumpPreviousBeat','jumpNextBeat']) $(id).disabled = !project || loading;
      $('previewLoop').disabled = mode !== 'preview' || !$('rangeEnabled').checked;
      $('loopHint').textContent = mode === 'preview' ? 'A–Bを繰り返して、配置やリズムを確認できます。' : 'ループ再生は確認モードで使えます。';
      renderBookmarks();
    }
  };
}
