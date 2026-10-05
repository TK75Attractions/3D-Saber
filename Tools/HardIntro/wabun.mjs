// Wabun: https://www.jarl.org/Japanese/A_Shiryo/A-C_Morse/morse.htm
// Timing: ITU-R M.1677-1 section 2.1 (1:3 tones, 1/3/7-unit spacing).
export const WABUN=Object.freeze({
  ア:'--.--',イ:'.-',ウ:'..-',エ:'-.---',オ:'.-...',カ:'.-..',キ:'-.-..',ク:'...-',ケ:'-.--',コ:'----',
  サ:'-.-.-',シ:'--.-.',ス:'---.-',セ:'.---.',ソ:'---.',タ:'-.',チ:'..-.',ツ:'.--.',テ:'.-.--',ト:'..-..',
  ナ:'.-.',ニ:'-.-.',ヌ:'....',ネ:'--.-',ノ:'..--',ハ:'-...',ヒ:'--..-',フ:'--..',ヘ:'.',ホ:'-..',
  マ:'-..-',ミ:'..-.-',ム:'-',メ:'-...-',モ:'-..-.',ヤ:'.--',ユ:'-..--',ヨ:'--',ラ:'...',リ:'--.',
  ル:'-.--.',レ:'---',ロ:'.-.-',ワ:'-.-',ヰ:'.-..-',ヱ:'.--..',ヲ:'.---',ン:'.-.-.',
  '\u3099':'..','\u309a':'..--.',ー:'.--.-'
});
export const MESSAGE='これを解読できたら私たちに教えてくれたら景品贈呈！';
// The exclamation has no JARL Wabun code. Transmit its exact reading without punctuation.
export const READING='コレヲカイドクデキタラ ワタシタチニオシエテクレタラ ケイヒンゾウテイ';
export function encodeWabun(reading){
  return Array.from(reading.normalize('NFD')).map(character=>{
    if(character===' ')return {character,code:null};
    const code=WABUN[character];if(!code)throw new Error('Unsupported Wabun character: '+character);
    return {character,code};
  });
}
export function makeTransmission(reading,start,end){
  if(!(end>start))throw new Error('Invalid transmission span');
  const letters=encodeWabun(reading),raw=[];let units=0;
  letters.forEach(({character,code},letterIndex)=>{
    if(!code){units+=4;return;}
    [...code].forEach((symbol,i)=>{
      const length=symbol==='-'?3:1;
      raw.push({character,letterIndex,symbol,unitStart:units,units:length});units+=length;
      if(i<code.length-1)units+=1;
    });
    if(letterIndex<letters.length-1)units+=3;
  });
  const unit=(end-start)/units;
  return {unit,units,letters,events:raw.map(e=>({...e,start:start+e.unitStart*unit,duration:e.units*unit})),start,end};
}
