 
(function(){
"use strict";

/* ---------- 规则常量 ---------- */
const MAX_SLOTS = 6;
const MAX_POINTS = 9;
const STORE_KEY = "taiwu_planner_builds_v1";

/* ---------- 数据索引 ---------- */
const D = WIKI_DATA;
const SKILL_BY_ID = new Map(D.skills.map(s => [s.id, s]));
const DROP_POOL = new Set(D.skill_drop_pool || []);
const PLAYER_SKILLS = D.skills.filter(s => DROP_POOL.has(s.id));
const EQUIP_BY_ID = new Map(D.equipment.map(e => [e.id, e]));
const CLEAN_EQUIP = D.equipment;
 
const SLOT_ORDER = ["武器", "护甲", "冠饰", "护手", "护足", "宝物", "宝物 2", "戒指", "手镯", "代步"];
const SLOT_CAT = s => (s === "宝物 2" ? "宝物" : s);
const RAR_ORDER = {"凡品": 0, "奇品": 1, "极品": 2, "绝品": 3};
const TRAIT_BY_ID = new Map(D.traits.map(t => [t.id, t]));
/* 数值词条（有属性名+数值区间的）按展示文本建索引——总览合计用 */
const TRAIT_BY_NAME = new Map(D.traits.filter(t => t.prop && t.range).map(t => [t.name, t]));
/* 天赋索引（摊平 6 层天赋树）——合计用 */
const TALENT_BY_ID = new Map(
  D.characters.flatMap(c => (c.talent_tree || []).flat()).map(t => [t.id, t]));
const ELIXIR_BY_ID = new Map(D.elixirs.map(x => [x.id, x]));
const TAG_ORDER = ["远程", "近战", "持续", "异常", "妨害", "增益", "光环", "追加", "弹射", "穿透", "中毒", "外伤", "神火", "破绽", "失神"];

/* ---------- 存档 ---------- */
function newBuild(){
  return { v: 1, name: "未命名构筑", char: null, talents: [], skills: [],
           pet: null, equips: {}, elixirs: [], note: "" };
}
function blankBuild(){
  const b = newBuild();
  return b;
}
function loadStore(){
  try{
    const raw = localStorage.getItem(STORE_KEY);
    if(raw){
      const s = JSON.parse(raw);
      if(s && Array.isArray(s.list) && s.list.length){
        s.list = s.list.map(b => sanitize(b));
        s.active = Math.min(Math.max(+s.active || 0, 0), s.list.length - 1);
        return s;
      }
    }
  }catch(e){}
  const b = blankBuild();
  return { list: [b], active: 0 };
}
function sanitize(b){
  // 载入/导入时的结构清洗：字段缺失补默认、非法条目剔除
  const base = newBuild();
  b = Object.assign(base, (b && typeof b === "object") ? b : {});
  if(!D.characters.some(c => c.id === b.char)) b.char = null;
  b.talents = Array.isArray(b.talents) ? b.talents.slice(0, 6) : [];
  b.skills = (Array.isArray(b.skills) ? b.skills : []).filter(x => x && SKILL_BY_ID.has(x.id) && DROP_POOL.has(x.id)).slice(0, MAX_SLOTS)
    .map(x => {
      const s = SKILL_BY_ID.get(x.id);
      const lv = Array.isArray(x.lv) ? x.lv.slice(0, s.upgrades.length).map(n => Math.max(0, Math.min(3, +n || 0))) : [];
      while(lv.length < s.upgrades.length) lv.push(0);
      let evo = x.evo && s.evolutions.some(e => e.id === x.evo) ? x.evo : null;
      return { id: x.id, evo, lv };
    });
  // 参悟点超限时从末尾分支回扣
  b.skills.forEach(en => trimPoints(en));
  b.pet = D.pets.some(p => p.id === b.pet) ? b.pet : null;
  b.equips = b.equips && typeof b.equips === "object" ? b.equips : {};
  for(const k in b.equips){
    if(!SLOT_ORDER.includes(k)){ delete b.equips[k]; continue; }
    let v = b.equips[k];
    if(typeof v === "string") v = { id: v, picks: {} };   // 旧格式（纯装备 id）
    if(!v || typeof v !== "object" || !EQUIP_BY_ID.has(v.id)){ delete b.equips[k]; continue; }
    v.picks = (v.picks && typeof v.picks === "object") ? v.picks : {};
    const pools = (EQUIP_BY_ID.get(v.id).random_trait_pools) || [];
    for(const pi in v.picks){
      const pool = pools[pi];
      if(!pool){ delete v.picks[pi]; continue; }
      v.picks[pi] = (Array.isArray(v.picks[pi]) ? v.picks[pi] : [])
        .filter(t => (pool.traits || []).includes(t))
        .slice(0, Math.max(0, +pool.count || 0));
    }
    b.equips[k] = v;
  }
  b.elixirs = (Array.isArray(b.elixirs) ? b.elixirs : []).filter(id => ELIXIR_BY_ID.has(String(id)));
  b.note = String(b.note || "");
  b.name = String(b.name || "未命名构筑").slice(0, 40);
  return b;
}
function trimPoints(en){
  const s = SKILL_BY_ID.get(en.id);
  let total = en.lv.reduce((a, b) => a + b, 0);
  for(let i = en.lv.length - 1; i >= 0 && total > MAX_POINTS; i--){
    const cut = Math.min(en.lv[i], total - MAX_POINTS);
    en.lv[i] -= cut; total -= cut;
  }
}

let store = loadStore();
let B = store.list[store.active];
let ui = { openSlot: -1, pickerQ: "", pickerTags: new Set(), showIO: false };

let saveTimer = null;
function save(){
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    try{ localStorage.setItem(STORE_KEY, JSON.stringify(store)); }catch(e){}
  }, 200);
}

/* ---------- 计算 ---------- */
function points(en){ return en.lv.reduce((a, b) => a + b, 0); }
function graduated(en){
  return points(en) >= MAX_POINTS && !!en.evo;
}
function finalTags(en){
  const s = SKILL_BY_ID.get(en.id);
  const set = new Set(s.tags || []);
  s.upgrades.forEach((branch, bi) => {
    for(let li = 0; li < en.lv[bi]; li++){
      (branch[li].add_tags || []).forEach(t => set.add(t));
    }
  });
  const evo = s.evolutions.find(e => e.id === en.evo);
  if(evo) (evo.add_tags || []).forEach(t => set.add(t));
  return set;
}
function branchName(branch){ return (branch[0].name || "").replace(/\s*等级\s*\d+\s*$/, "").trim(); }
function charOf(){ return D.characters.find(c => c.id === B.char) || null; }
function skillBadgeHtml(en){
  const s = SKILL_BY_ID.get(en.id);
  const tags = [...finalTags(en)].sort((a, b) => {
    const ia = TAG_ORDER.indexOf(a), ib = TAG_ORDER.indexOf(b);
    return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib);
  });
  return tags.map(t => {
    const base = (s.tags || []).includes(t);
    return "<span class='badge" + (base ? "" : " x") + "'>" + esc(t) + "</span>";
  }).join("");
}
/* 导出前检查：已选装备的目标词条是否每池勾满（未勾满时 JSON 发装备会缺词条） */
function equipUnpickedList(){
  const out = [];
  SLOT_ORDER.forEach(s => {
    const o = equipOf(s);
    if(!o) return;
    const e = EQUIP_BY_ID.get(o.id);
    const miss = [];
    (e.random_trait_pools || []).forEach((pool, pi) => {
      const need = +pool.count || 0;           // 抽取数为 0 的死池（三元一气珠池B）不算缺
      const got = ((o.picks || {})[pi] || []).length;
      if(need > 0 && got < need) miss.push("池" + (pi + 1) + " 已勾 " + got + "/" + need);
    });
    if(miss.length) out.push(e.name + "（" + s + "）：" + miss.join("，"));
  });
  return out;
}
function confirmPicksComplete(){
  const miss = equipUnpickedList();
  if(!miss.length) return true;
  return confirm("以下装备的目标词条未勾满：\n\n" + miss.join("\n") +
    "\n\n未勾满时导出的 JSON 词条不全，游戏内「JSON 发装备」会缺词条。\n仍要继续复制吗？");
}

function equipTraitLine(t){
  if(t == null) return "";
  if(t.name) return "<span class='pl-etrait'>" + rich(t.name) + "</span>";
  const tr = TRAIT_BY_ID.get(t.id);
  const raw = (tr && (tr.desc || tr.name)) || "";
  if(!raw){
    // DB 无文案无 buff、代码无字符串引用（大鳌逆鳞兜/九凤朱翎/白熊/鹰撮环/结海楼 5 件占位残留）
    return "<span class='pl-etrait fx' style='color:var(--text-faint)'>✦ 该装备的紫色词条来自随机词条池</span>";
  }
  const plain = String(raw).replace(/<[^>]*>/g, "");
  return "<span class='pl-etrait fx' title='" + esc(plain) + "'>✦ " + rich(raw) + "</span>";
}

/* ---------- 已选装备属性合计（数值词条按属性聚合） ---------- */
const PL_RANGE_SUF = /\s+-?[\d.]+%?(?:~-?[\d.]+%?)?$/;   // 词条文本尾部的数值区间
function plParseRange(s){            // "3~6"|"1.5%~3%"|"-5~-5"|"1" -> [lo,hi,pct] | null
  if(!s) return null;
  const pct = s.indexOf("%") >= 0;
  const parts = String(s).replace(/%/g, "").split("~").map(Number);
  if(!parts.length || parts.some(x => isNaN(x))) return null;
  return [parts[0], parts.length > 1 ? parts[1] : parts[0], pct];
}
/* 词条文本（展示串）-> {key, pct, r:[[lo,hi]×3]}；特效/非数值返回 null。
   key=去掉尾部区间的属性名（含分类前缀），%与点数分桶避免 最大生命值% 与 点数 混加 */
function plTraitStat(line){
  if(!line) return null;
  const tr = TRAIT_BY_NAME.get(line);
  if(!tr) return null;
  const r1 = plParseRange(tr.range);
  if(!r1) return null;
  const r2 = plParseRange(tr.range_t2) || r1;
  const r3 = plParseRange(tr.range_t3) || r1;
  return {
    key: line.replace(PL_RANGE_SUF, "") + (r1[2] ? "\t%" : ""),
    pct: r1[2],
    r: [[r1[0], r1[1]], [r2[0], r2[1]], [r3[0], r3[1]]]
  };
}
/* buff/天赋/参悟 deltas 的数值列是否百分比（按列名判，值域无关：CriticalChance=1.0 是 100%）；
   百分比列进合计前 ×100 对齐装备词条的百分点单位。
   三个漏网键：Fire_ExtraArea（神火爆范围%，无 Percent 后缀）、Aure_AreaPercantAddition
   （官方拼写错 Percant）、Additional_CritDamageAddition（追加暴伤%，只写 Crit） */
const PL_PCT_KEY = /Percent|Chance|Rate|Reduction|Multiply|Critical|ExtraArea|Percant|^Additional_CritDamageAddition$/i;
function plIsPctKey(key){ return PL_PCT_KEY.test(key || ""); }

 
/* 独立乘区字段：值=乘数本身（素女天音 U1L3=1.2 即 ×1.2，文案写 1.25 虚标），
   槽聚合 (值+1) 连乘——展示 ×N、多来源连乘，不并入百分比加法池 */
const PL_MULT_KEY = /^Skill_DamageMultiply$/;
function isMultKey(key){ return PL_MULT_KEY.test(key || ""); }
function fmtGainVal(d, v){
  const n = v == null ? parseFloat(d.value) : v;
  if(isNaN(n)) return d.value;
  if(isMultKey(d.key)) return "×" + (Math.round(n * 100) / 100);
  const pct = plIsPctKey(d.key);
  const f = Math.round((pct ? n * 100 : n) * 1000) / 1000;
  return (f >= 0 ? "+" : "") + f + (pct ? "%" : "");
}
/* 增益系命名状态池（与总览 namedBuffs("1") 同口径）——参悟/进化会给予的增益 buff。
   排除 scope=self 的（龙吐珠龙珠=本功法伤害专属增益，不算全局收益） */
const GAIN_BUFF_POOL = new Set(D.buffs.filter(b =>
  (b.tags || [])[0] === "1" && +b.max_stack >= 1 && (b.deltas || []).length && b.name
  && b.scope !== "self"
).map(b => b.id));
/* 妨害系命名状态池（namedBuffs("3") 同口径）——参悟/进化会向敌方施加的减益 */
const CURSE_BUFF_POOL = new Set(D.buffs.filter(b =>
  (b.tags || [])[0] === "3" && +b.max_stack >= 1 && (b.deltas || []).length && b.name
  && b.scope !== "self"
).map(b => b.id));
const BUFF_KINDS = [
  { kind: "gain", pool: GAIN_BUFF_POOL, random: "随机增益", cls: "gb" },
  { kind: "curse", pool: CURSE_BUFF_POOL, random: "随机妨害", cls: "cb" },
];
const GAIN_BUFF_NAME = id => ((D.buffs.find(b => b.id === id) || {}).name) || id;
/* 给予/施加形式判名：desc 明说「随机」且多候选 → 随机增益/妨害行；否则列具体名（顿号连接） */
function gainBuffRowName(gains, txt, randomLabel){
  if(gains.length >= 2 && /随机/.test(txt || "")) return randomLabel;
  return gains.map(GAIN_BUFF_NAME).join("、");
}
/* 按分支分组收集该功法全部可提供的全局收益/施加（点满口径，与当前勾选无关）。
   每分支一行：数值 delta 按同线 L1+L2+L3 求和（乘区连乘），增益/妨害按 ✦ 行 */
function skillGlobalByBranch(s){
  const out = [];
  const collect = (units, label) => {
    const acc = new Map();
    const rowKey = (cls, disp, cond) => (cls || "") + "\t" + disp + "\t" + (cond || "");
    units.forEach(u => {
      const txt = plainText(u.desc);
      (u.deltas || []).forEach(d => {
        if(d.scope === "self") return;
        const k = rowKey("", d.stat, d.cond);
        let e = acc.get(k);
        if(!e){ e = { disp: d.stat, key: d.key, cond: d.cond || "", depend: d.depend || "",
                      v: isMultKey(d.key) ? 1 : 0, txt };
          acc.set(k, e); }
        const v = parseFloat(d.value);
        if(!isNaN(v)) e.v = isMultKey(d.key) ? e.v * v : e.v + v;
        if(d.depend) e.depend = d.depend;
      });
      BUFF_KINDS.forEach(kk => {
        const gains = (u.add_buffs || []).filter(x => kk.pool.has(x));
        if(!gains.length) return;
        const disp = gainBuffRowName(gains, txt, kk.random);
        const k = rowKey(kk.cls, disp, "");
        if(!acc.has(k)) acc.set(k, { disp, cls: kk.cls, gains, txt });
      });
    });
    const items = [...acc.values()];
    if(items.length) out.push({ label, items });
  };
  (s.upgrades || []).forEach((br, bi) => collect(br, branchName(br)));
  (s.evolutions || []).forEach(e => collect([e], "进化·" + e.name));
  return out;
}

/* 汇总一件装备的数值词条行（固有 brief + 已勾目标词条），逐行喂给 agg */
function plCollectEquipLines(e, o, add){
  (e.fixed_traits || []).forEach(t => { if(t && t.prop && t.name) add(t.name, e); });
  (e.random_trait_pools || []).forEach((pool, pi) => {
    (((o && o.picks) || {})[pi] || []).forEach(txt => add(txt, e));
  });
}
function plFmtSum(v, pct){
  const f = x => String(Math.round(x * 1000) / 1000);
  return f(v[0]) + (v[0] !== v[1] ? "~" + f(v[1]) : "") + (pct ? "%" : "");
}

/* ---------- 样式 ---------- */
const CSS = `
.pl-wrap{display:grid;grid-template-columns:minmax(0,1fr) 330px;gap:16px;align-items:start}
@media(max-width:1100px){.pl-wrap{grid-template-columns:1fr}}
.pl-side{position:sticky;top:16px;max-height:calc(100vh - 32px);overflow-y:auto}
.pl-toolbar{display:flex;flex-wrap:wrap;gap:8px;align-items:center}
.pl-toolbar input[type=text]{background:var(--panel);color:var(--text);border:1px solid var(--line2);
  border-radius:6px;padding:5px 10px;font-size:14px;width:180px;font-family:inherit}
.pl-toolbar select{background:var(--panel);color:var(--text);border:1px solid var(--line2);
  border-radius:6px;padding:5px 8px;font-size:13px;font-family:inherit;max-width:220px}
.pl-btn{background:var(--panel2);color:var(--text-dim);border:1px solid var(--line2);border-radius:6px;
  padding:4px 12px;font-size:13px;cursor:pointer;font-family:inherit;letter-spacing:1px}
.pl-btn:hover{color:var(--gold);border-color:var(--gold-dim)}
.pl-btn.warn:hover{color:var(--red);border-color:var(--red)}
.pl-statsum{margin:6px 0 4px;border:1px solid var(--line2);border-radius:8px;overflow:hidden;font-size:13px;
  display:grid;grid-template-columns:max-content max-content 30px;gap:0 12px;width:max-content;max-width:100%}
.pl-statsum .pl-shd,.pl-statsum .pl-srow{display:contents}
.pl-statsum .pl-shd span{background:var(--panel2);color:var(--text-faint);font-size:12px;letter-spacing:1px;padding:5px 10px}
.pl-statsum .pl-srow span{padding:5px 10px;border-top:1px solid var(--line)}
.pl-statsum .sn{text-align:right;color:var(--text-faint)}
.pl-statsum .sn.tg{cursor:pointer;text-decoration:underline dotted var(--text-faint)}
.pl-statsum .sv{text-align:left;color:var(--gold);font-variant-numeric:tabular-nums}
.pl-statsum .snm{white-space:nowrap}
.pl-statsum .pl-srow.sub{display:none}
.pl-statsum .pl-srow.sub .snm{color:var(--text-dim);padding-left:26px}
.pl-statsum .pl-srow.sub .sv{color:var(--text-dim);font-size:12px}
.pl-statsum .pl-srow.open .sn.tg{color:var(--gold);text-decoration-color:var(--gold)}
.pl-ov2col{display:grid;grid-template-columns:max-content minmax(280px,1fr);gap:18px;align-items:start;margin:6px 0}
@media(max-width:1100px){.pl-ov2col{grid-template-columns:1fr}}
.pl-buffcalc{border:1px solid var(--line2);border-radius:8px;padding:10px 12px;font-size:13px}
.pl-buffcalc .bhd{font-size:13px;letter-spacing:1px;margin-bottom:6px;display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.pl-buffcalc .bhd:not(:first-child){margin-top:12px;border-top:1px solid var(--line2);padding-top:10px}
.pl-buffcalc .btag{font-size:11px;color:var(--text-faint);border:1px solid var(--line2);border-radius:4px;padding:1px 6px}
.pl-buffcalc .btag b{color:var(--gold)}
.pl-buffcalc input{background:var(--panel);color:var(--text);border:1px solid var(--line2);
  border-radius:6px;padding:2px 6px;font-size:12px;font-family:inherit;width:48px;text-align:right}
.pl-buffcalc .bmeta{color:var(--text-faint);font-size:12px;margin-bottom:6px;display:flex;gap:6px;align-items:center;flex-wrap:wrap}
.pl-buffcalc .bmeta b{color:var(--gold)}
.pl-buffcalc .brow{display:grid;grid-template-columns:max-content 52px minmax(0,1fr);gap:8px;
  padding:5px 0;border-top:1px solid var(--line);align-items:baseline}
.pl-buffcalc .bnm{white-space:nowrap}
.pl-buffcalc .blayn{text-align:right;color:var(--text);font-variant-numeric:tabular-nums}
.pl-buffcalc .blayn .text-faint{color:var(--text-faint)}
.pl-buffcalc .bstats{display:flex;flex-direction:column;gap:2px}
.pl-buffcalc .bstat3{display:grid;grid-template-columns:minmax(0,1fr) 64px 84px 76px;gap:6px;align-items:baseline}
.pl-buffcalc .bstat3.bh3{color:var(--text-faint);font-size:11px;margin:2px 0}
.pl-buffcalc .bstat3 .s-raw,.pl-buffcalc .bstat3 .s-fin,.pl-buffcalc .bstat3 .s-up{text-align:right;font-variant-numeric:tabular-nums}
.pl-buffcalc .bstat3 .s-nm{color:var(--text-dim);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.pl-buffcalc .bstat3 .s-raw{color:var(--text-faint)}
.pl-buffcalc .bstat3 .s-fin{color:var(--gold);font-weight:600}
.pl-buffcalc .bstat3 .s-up{color:#6fd66f}
.pl-chargrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:10px}
.pl-char{display:flex;align-items:center;gap:10px;background:var(--panel);border:1px solid var(--line);
  border-radius:8px;padding:8px 10px;cursor:pointer}
.pl-char img{width:42px;height:42px;border-radius:6px;object-fit:cover;flex:none}
.pl-char .nm{color:var(--gold);font-weight:600;font-size:14px}
.pl-char .tt{font-size:11px;color:var(--text-faint)}
.pl-char.on{border-color:var(--gold);background:#c9a86a14}
.pl-char:hover{border-color:var(--gold-dim)}
.pl-talrow{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:8px;align-items:stretch}
.pl-tt{flex:none;width:52px;color:var(--gold-dim);font-size:12px;padding-top:9px;letter-spacing:1px}
.pl-tal{flex:1;min-width:180px;max-width:280px;background:var(--panel);border:1px solid var(--line);
  border-radius:7px;padding:8px 11px;cursor:pointer;position:relative}
.pl-tal:hover{border-color:var(--gold-dim)}
.pl-tal.on{border-color:var(--gold);background:#c9a86a14}
.pl-tal.on::after{content:"✓";position:absolute;top:6px;right:9px;color:var(--gold);font-weight:700}
.pl-tal .nm{color:var(--gold);font-weight:600;font-size:13.5px}
.pl-tal .ds{font-size:12px;color:var(--text-dim);line-height:1.55}
.pl-tal .ds .kwx{border-bottom-color:var(--gold-dim)}
.pl-slotgrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(290px,1fr));gap:10px}
.pl-slot{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:10px 12px}
.pl-slot .empty{color:var(--text-faint);font-size:13px;text-align:center;padding:14px 0;cursor:pointer}
.pl-slot .empty:hover{color:var(--gold)}
.pl-skhead{display:flex;align-items:center;gap:9px;flex-wrap:wrap}
.pl-skhead img{width:38px;height:38px;border-radius:6px;background:#ffffff08;padding:2px}
.pl-skhead .nm{color:var(--gold);font-weight:600;font-size:15px}
.pl-skhead .ops{margin-left:auto;display:flex;gap:4px}
.pl-mini{background:none;border:1px solid var(--line2);color:var(--text-faint);border-radius:5px;
  padding:1px 7px;font-size:12px;cursor:pointer;font-family:inherit;line-height:1.5}
.pl-mini:hover{color:var(--gold);border-color:var(--gold-dim)}
.pl-grad{font-size:11px;padding:0 7px;border-radius:4px;line-height:19px;border:1px solid var(--green);
  color:var(--green)}
.pl-grad.half{border-color:var(--text-faint);color:var(--text-faint)}
.pl-evorow{display:flex;gap:6px;flex-wrap:wrap;margin-top:8px}
.pl-evo{flex:1;min-width:150px;background:var(--bg2);border:1px dashed var(--line2);border-radius:7px;
  padding:7px 10px;cursor:pointer}
.pl-evo:hover{border-color:var(--gold-dim)}
.pl-evo.on{border-style:solid;border-color:var(--gold);background:#c9a86a14}
.pl-evo .nm{color:var(--r-legend);font-weight:600;font-size:13px}
.pl-evo .ds{font-size:12px;color:var(--text-dim);line-height:1.5}
.pl-branch{display:flex;gap:9px;align-items:flex-start;padding:7px 0;border-top:1px solid var(--line)}
.pl-branch:first-of-type{border-top:none}
.pl-branch .bi{flex:none;width:34px;height:34px;object-fit:contain;border-radius:5px;background:#ffffff08;padding:2px}
.pl-branch .body{flex:1;min-width:0}
.pl-branch .nm{color:var(--gold);font-weight:600;font-size:13px}
.pl-branch .ds{font-size:12.5px;color:var(--text-dim);line-height:1.55}
.pl-step{display:flex;align-items:center;gap:6px;flex:none;margin-top:3px}
.pl-step b{font-size:12px;color:var(--text-dim);min-width:24px;text-align:center}
.pl-pips{display:inline-flex;gap:3px;vertical-align:middle}
.pl-pip{width:16px;height:8px;border-radius:2px;background:var(--line)}
.pl-pip.on{background:var(--gold)}
.pl-picker{margin-top:10px;border:1px solid var(--line2);border-radius:8px;background:var(--bg2);padding:12px}
.pl-picker .filters input{width:100%}
.pl-pgrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(210px,1fr));gap:8px;margin-top:10px;max-height:340px;overflow-y:auto}
.pl-pcard{background:var(--panel);border:1px solid var(--line);border-radius:7px;padding:8px 10px;cursor:pointer}
.pl-pcard:hover{border-color:var(--gold-dim)}
.pl-pcard.dis{opacity:.35;cursor:not-allowed}
.pl-pcard .nm{color:var(--gold);font-weight:600;font-size:13.5px}
.pl-pcard .sub{font-size:11.5px;color:var(--text-faint);margin-top:2px}
.pl-petgrid{display:flex;gap:8px;flex-wrap:wrap}
.pl-pet{background:var(--panel);border:1px solid var(--line);border-radius:7px;padding:6px 12px;cursor:pointer;font-size:13px}
.pl-pet:hover{border-color:var(--gold-dim)}
.pl-pet.on{border-color:var(--gold);background:#c9a86a14;color:var(--gold)}
.pl-pet .kd{font-size:11px;color:var(--text-faint)}
.pl-eqrow{display:grid;grid-template-columns:56px minmax(0,1fr);gap:10px;align-items:start;
  padding:8px 0;border-top:1px solid var(--line)}
.pl-eqrow:first-child{border-top:none}
.pl-eqrow .st{color:var(--gold-dim);font-size:13px;padding-top:5px;letter-spacing:1px}
.pl-eqrow select{background:var(--panel);color:var(--text);border:1px solid var(--line2);border-radius:6px;
  padding:4px 8px;font-size:13px;font-family:inherit;width:100%;max-width:360px}
.pl-eqinfo{font-size:12.5px;color:var(--text-dim);margin-top:5px;line-height:1.6}
.pl-eqinfo .pool{font-size:12px;color:var(--text-faint)}
.pl-chipbar{display:flex;gap:6px;flex-wrap:wrap}
.pl-chip{border:1px solid var(--line2);border-radius:5px;padding:2px 10px;font-size:13px;cursor:pointer;
  color:var(--text-dim);background:var(--panel)}
.pl-chip:hover{border-color:var(--gold-dim);color:var(--text)}
.pl-chip.on{border-color:var(--gold);color:var(--gold);background:#c9a86a14}
.pl-chip.off{opacity:.4}
.pl-note{width:100%;min-height:70px;background:var(--panel);color:var(--text-dim);border:1px solid var(--line2);
  border-radius:6px;padding:8px 10px;font-size:13px;font-family:inherit;resize:vertical;line-height:1.6}
.pl-io{width:100%;min-height:90px;background:var(--bg2);color:var(--text-dim);border:1px solid var(--line2);
  border-radius:6px;padding:8px 10px;font-size:12px;font-family:Consolas,monospace;resize:vertical}
.pl-side .panel{margin-bottom:12px;padding:14px 16px}
.pl-check{display:flex;justify-content:space-between;gap:8px;font-size:13px;color:var(--text-dim);
  padding:3px 0;border-bottom:1px dashed #3a312566}
.pl-check b{font-weight:600}
.pl-ok{color:var(--green)} .pl-no{color:var(--text-faint)} .pl-bad{color:var(--red)}
.pl-tagstat{display:flex;gap:5px;flex-wrap:wrap;margin-top:6px}
.pl-sumlist{font-size:13px;color:var(--text-dim);line-height:1.8}
.pl-sumlist a{color:var(--gold)}
.pl-sumlist .h{color:var(--gold-dim);letter-spacing:1px;font-size:12px;margin-top:6px}
/* ---- 选择器弹窗（master-detail，参考 POB/maxroll 物品·技能选择）---- */
body.pl-modal-open{overflow:hidden}
.pl-mov{position:fixed;inset:0;background:#000c;z-index:60;display:flex;align-items:center;justify-content:center;padding:22px}
.pl-modal{background:var(--bg2);border:1px solid var(--gold-dim);border-radius:10px;width:min(1280px,95vw);
  height:min(840px,92vh);display:flex;flex-direction:column;overflow:hidden;box-shadow:0 12px 60px #000c}
.pl-mhead{padding:12px 16px;border-bottom:1px solid var(--line);display:flex;flex-wrap:wrap;gap:8px;row-gap:6px;align-items:center}
.pl-mhead .mtitle{color:var(--gold);font-size:16px;font-weight:600;letter-spacing:2px;margin-right:6px}
.pl-mhead input,.pl-mhead select{background:var(--panel);color:var(--text);border:1px solid var(--line2);
  border-radius:6px;padding:6px 10px;font-size:13px;font-family:inherit}
.pl-mhead input{flex:1;min-width:150px;max-width:300px}
.pl-mclose{margin-left:auto;background:none;border:1px solid var(--line2);color:var(--text-dim);border-radius:6px;
  width:30px;height:30px;font-size:15px;cursor:pointer;font-family:inherit}
.pl-mclose:hover{color:var(--red);border-color:var(--red)}
.pl-mbody{flex:1;display:grid;grid-template-columns:minmax(300px,380px) minmax(0,1fr);min-height:0}
@media(max-width:900px){.pl-mbody{grid-template-columns:1fr}}
.pl-mlist{overflow-y:auto;border-right:1px solid var(--line);padding:10px;display:flex;flex-direction:column;gap:6px}
.pl-mitem{display:flex;gap:10px;align-items:center;background:var(--panel);border:1px solid var(--line);
  border-radius:8px;padding:8px 10px;cursor:pointer;text-align:left}
.pl-mitem:hover{border-color:var(--gold-dim)}
.pl-mitem.on{border-color:var(--gold);background:#c9a86a14}
.pl-mitem img{width:42px;height:42px;object-fit:contain;border-radius:6px;background:#ffffff08;padding:2px;flex:none}
.pl-mitem .nm{color:var(--gold);font-weight:600;font-size:14px}
.pl-mitem .sub{font-size:11.5px;color:var(--text-faint);line-height:1.5;margin-top:1px}
.pl-mitem .right{margin-left:auto;flex:none;text-align:right;font-size:11.5px;color:var(--text-dim);line-height:1.6}
.pl-mdetail{overflow-y:auto;padding:16px 22px;position:relative}
.pl-md-hd{display:flex;gap:14px;align-items:center;flex-wrap:wrap;margin-bottom:10px}
.pl-md-hd img{width:64px;height:64px;object-fit:contain;border-radius:8px;background:#ffffff08;padding:3px}
.pl-md-hd .nm{color:var(--gold);font-size:22px;font-weight:600}
.pl-md-hd .row{font-size:12.5px;color:var(--text-faint);margin-top:3px;display:flex;gap:6px;flex-wrap:wrap;align-items:center}
.pl-mdsec{color:var(--gold);font-size:14px;letter-spacing:2px;margin:18px 0 8px;border-bottom:1px solid var(--line);padding-bottom:4px}
.pl-mdsec:first-child{margin-top:0}
.pl-mdesc{font-size:14px;color:var(--text-dim);line-height:1.8}
.pl-ubranch{border:1px solid var(--line);border-radius:8px;margin-bottom:8px;overflow:hidden}
.pl-ubranch .bh{display:flex;align-items:center;gap:8px;padding:7px 12px;background:var(--panel);cursor:pointer}
.pl-ubranch .bh:hover{background:var(--panel2)}
.pl-ubranch .bh img{width:28px;height:28px;object-fit:contain;border-radius:5px;background:#ffffff08;padding:2px}
.pl-ubranch .bh .bn{color:var(--gold);font-weight:600;font-size:13.5px}
.pl-ubranch .bc{padding:6px 12px 10px;display:none}
.pl-ubranch.open .bc{display:block}
.pl-ulv{display:flex;gap:8px;align-items:flex-start;padding:5px 0;border-top:1px dashed #3a312566;font-size:13px;color:var(--text-dim);line-height:1.7}
.pl-ulv:first-child{border-top:none}
.pl-ulv .lv{flex:none;width:30px;height:20px;border:1px solid var(--line2);border-radius:4px;color:var(--gold-dim);
  font-size:11px;line-height:19px;text-align:center;margin-top:2px}
.pl-evocard{border:1px solid var(--line);border-radius:8px;padding:10px 13px;margin-bottom:8px;background:var(--panel)}
.pl-evocard .nm{color:var(--r-legend);font-size:15px;font-weight:600;margin-bottom:4px}
.pl-evocard .ds{font-size:13px;color:var(--text-dim);line-height:1.7}
.pl-poolrow{font-size:13px;color:var(--text-dim);line-height:1.9;padding-left:4px}
.pl-poolrow b{color:var(--gold);font-weight:600}
.pl-mfoot{position:sticky;bottom:0;margin-top:14px;padding:12px 0 2px;background:linear-gradient(transparent,var(--bg2) 30%);
  display:flex;gap:10px;justify-content:flex-end}
/* 弹窗内可选交互（分支/进化/词条）*/
.pl-ubranch.pick{cursor:pointer}
.pl-ubranch.pick:hover{border-color:var(--gold-dim)}
.pl-ubranch.pick.on{border-color:var(--gold);background:#c9a86a12}
.pl-ubranch .bh .selmark{margin-left:auto;color:var(--gold);font-weight:700;font-size:14px;visibility:hidden;flex:none}
.pl-ubranch.pick.on .bh .selmark{visibility:visible}
.pl-evocard.pick{cursor:pointer}
.pl-evocard.pick:hover{border-color:var(--gold-dim)}
.pl-evocard.pick.on{border-color:var(--gold);background:#c9a86a14;box-shadow:inset 0 0 0 1px var(--gold)}
.pl-evocard.pick .pickmark{float:right;color:var(--gold);visibility:hidden;font-size:14px}
.pl-evocard.pick.on .pickmark{visibility:visible}
.pl-status{font-size:12.5px;color:var(--text-faint);margin-right:auto;align-self:center;line-height:1.5}
.pl-status b{color:var(--gold);font-weight:600}
.pl-tchips{display:flex;gap:9px;flex-wrap:wrap;margin:8px 0 4px}
.pl-tchip{border:1px solid var(--line2);border-radius:6px;padding:5px 13px;font-size:14px;cursor:pointer;
  color:var(--text-dim);background:var(--panel);user-select:none;line-height:1.4}
.pl-tchip:hover{border-color:var(--gold-dim);color:var(--text)}
.pl-tchip.on{border-color:var(--gold);color:var(--gold);background:#c9a86a14;font-weight:600}
.pl-tchip.flash{animation:plflash .35s}
@keyframes plflash{0%,100%{border-color:var(--line2)}50%{border-color:var(--red)}}
.pl-mitem .fxline{font-size:11px;color:var(--text-dim);line-height:1.45;margin-top:2px;overflow:hidden;
  display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical}
.pl-mitem .fxline .fmark{color:var(--r-epic)}
.pl-ovrow{border-top:1px solid var(--line);padding:9px 0}
.pl-ovrow .ovhd{display:flex;align-items:center;gap:9px;flex-wrap:wrap}
.pl-ovrow img.ovicon{width:30px;height:30px;object-fit:contain;border-radius:5px;background:#ffffff08;padding:2px}
.pl-ovrow .ovslot{font-size:11.5px;color:var(--text-faint)}
.pl-ovrow .ovlist{font-size:12.5px;color:var(--text-dim);line-height:1.8;margin-top:4px;padding-left:4px}
.pl-eqcard img{width:32px;height:32px;object-fit:contain;border-radius:5px;background:#ffffff08;padding:2px;flex:none}
.pl-slotbranch{display:flex;gap:8px;align-items:flex-start;padding:6px 0;border-top:1px solid var(--line)}
.pl-slotbranch img{flex:none;width:28px;height:28px;object-fit:contain;border-radius:5px;background:#ffffff08;padding:2px}
.pl-slotbranch .nm{color:var(--gold);font-weight:600;font-size:13px}
.pl-slotbranch .ds{font-size:12.5px;color:var(--text-dim);line-height:1.55}
.pl-evoone{border:1px solid var(--gold-dim);border-radius:7px;padding:6px 10px;margin-top:8px;background:#c9a86a08}
.pl-evoone .nm{color:var(--r-legend);font-weight:600;font-size:13.5px}
.pl-evoone .ds{font-size:12.5px;color:var(--text-dim);line-height:1.55}
.pl-tip{font-size:13px;color:var(--text-dim)}
/* ---- 功法卡·全局收益（按分支分组列全部可提供项）---- */
.pl-slotglobal{margin-top:8px;padding-top:7px;border-top:1px dashed #3a312566;
  display:flex;flex-direction:column;gap:4px}
.pl-slotglobal .hd{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
.pl-slotglobal .gl{flex:none;font-size:11px;letter-spacing:1px;color:var(--green);
  border:1px solid var(--green);border-radius:4px;padding:0 6px;line-height:19px}
.pl-gline{display:flex;gap:6px;align-items:flex-start;flex-wrap:wrap}
.pl-gline .bl{flex:none;font-size:11px;color:var(--gold-dim);min-width:70px;padding-top:3px;
  letter-spacing:.5px;white-space:nowrap}
.pl-gchip{border:1px solid var(--line2);border-radius:5px;padding:1px 8px;font-size:12px;
  color:var(--text-dim);background:var(--panel);white-space:nowrap;cursor:help}
.pl-gchip b{color:var(--green);font-weight:600;margin-left:4px;font-variant-numeric:tabular-nums}
.pl-gchip i{font-style:normal;font-size:10.5px;color:var(--text-faint);margin-left:4px}
.pl-gchip.dep{border-style:dashed}
.pl-gchip.dep b{color:var(--text-faint)}
.pl-gchip.gb b{color:var(--r-epic)}
.pl-gchip.cb b{color:var(--red)}
.pl-cellgrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:10px;margin-top:2px}
.pl-cell{background:var(--panel);border:1px solid var(--line);border-radius:9px;padding:10px 13px;cursor:pointer;position:relative}
.pl-cell:hover{border-color:var(--gold-dim)}
.pl-cell.on{border-color:var(--gold-dim);background:#c9a86a0d}
.pl-cell .empty{color:var(--text-faint);font-size:13px;text-align:center;padding:16px 0}
.pl-cell .empty:hover{color:var(--gold)}
.pl-cell .hd{display:flex;align-items:center;gap:9px;flex-wrap:wrap}
.pl-cell .hd .nm{color:var(--gold);font-weight:600;font-size:15px}
.pl-cell .hd img{width:56px;height:56px;object-fit:contain;border-radius:6px;background:#ffffff08;padding:2px;flex:none}
.pl-cell .ds{font-size:12.5px;color:var(--text-dim);line-height:1.6;margin-top:5px}
.pl-erow .kvline{font-size:12.5px;color:var(--text-faint);margin-top:2px}
.pl-erow .obtain{font-size:12px;color:var(--text-faint);margin-top:4px;border-top:1px dashed #3a312566;padding-top:4px}
\n.pl-modal.single .pl-mbody{grid-template-columns:1fr}
.pl-erow{display:flex;gap:12px;align-items:flex-start;background:var(--panel);border:1px solid var(--line);
  border-radius:9px;padding:10px 14px;margin-bottom:8px}
.pl-erow.on{border-color:var(--gold);background:#c9a86a10}
.pl-erow img{width:64px;height:64px;object-fit:contain;border-radius:6px;background:#ffffff08;padding:2px;flex:none;margin-top:2px}
.pl-erow .mid{flex:1;min-width:0}
.pl-erow .nm{color:var(--gold);font-weight:600;font-size:15px}
.pl-erow .ds{font-size:13.5px;color:var(--text-dim);line-height:1.7;margin-top:3px}
.pl-erow .pl-btn{flex:none;margin-top:6px;min-width:64px}

.pl-btn.primary{border-color:var(--gold);color:var(--gold)}
.pl-btn.primary:hover{background:#c9a86a20}
.pl-eqcard{display:flex;align-items:center;gap:10px;flex-wrap:wrap}
.pl-eqname{color:var(--gold);font-weight:600;font-size:14.5px}
.pl-mempty{padding:40px 20px;text-align:center;color:var(--text-faint)}
`;
function injectCss(){
  if(document.getElementById("pl-css")) return;
  const st = document.createElement("style");
  st.id = "pl-css";
  st.textContent = CSS;
  document.head.appendChild(st);
}

/* ---------- 渲染 ---------- */
let ROOT = null;

window.pgPlanner = function(c){
  injectCss();
  pageHead(c, "BD 构筑台", "自选角色、境界天赋、功法参悟与进化、装备和丹药，搭一套自己的 BD · 规则与游戏一致（功法 6 槽 · 每门参悟 9 点 · 进化三选一）· 构筑自动保存在本浏览器");
  ROOT = el("div");
  c.appendChild(ROOT);
  renderAll();
};

function renderAll(){
  if(!ROOT) return;
  // 工具栏（重画会丢输入焦点：输入类控件只在创建时绑定，重画前保留其值）
  ROOT.innerHTML = "";
  ROOT.appendChild(renderToolbar());
  const wrap = el("div", "pl-wrap");
  const main = el("div");
  renderEdit(main);
  const side = el("div", "pl-side");
  renderSide(side);
  wrap.append(main, side);
  ROOT.appendChild(wrap);
  if(ui.showIO){
    const io = renderIO();
    ROOT.appendChild(io);
  }
}

function renderToolbar(){
  const bar = el("div", "panel pl-toolbar");
  const nameInp = el("input"); nameInp.type = "text"; nameInp.value = B.name; nameInp.placeholder = "构筑名";
  nameInp.oninput = () => { B.name = nameInp.value.slice(0, 40) || "未命名构筑"; save(); };
  const sel = el("select");
  sel.innerHTML = store.list.map((b, i) =>
    "<option value='" + i + "'" + (i === store.active ? " selected" : "") + ">" +
    esc(b.name) + "（" + buildSummaryTag(b) + "）</option>").join("");
  sel.onchange = () => {
    store.active = +sel.value;
    B = store.list[store.active];
    ui.openSlot = -1;
    save(); renderAll();
  };
  const bNew = el("button", "pl-btn", "新建");
  bNew.onclick = () => {
    const b = blankBuild();
    store.list.push(b); store.active = store.list.length - 1; B = b;
    save(); renderAll();
  };
  const bCopy = el("button", "pl-btn", "复制当前");
  bCopy.onclick = () => {
    const b = sanitize(JSON.parse(JSON.stringify(B)));
    b.name = B.name + " 副本";
    store.list.push(b); store.active = store.list.length - 1; B = b;
    save(); renderAll();
  };
  const bDel = el("button", "pl-btn warn", "删除");
  bDel.onclick = () => {
    if(store.list.length <= 1){ B = sanitize(newBuild()); store.list = [B]; store.active = 0; }
    else { store.list.splice(store.active, 1); store.active = Math.max(0, store.active - 1); B = store.list[store.active]; }
    save(); renderAll();
  };
  const bCopyJson = el("button", "pl-btn", "复制构筑");
  bCopyJson.title = "把当前构筑复制为 JSON 文本（分享给别人，粘到导出/导入面板即可导入）";
  bCopyJson.onclick = async () => {
    if(!confirmPicksComplete()) return;
    const txt = JSON.stringify(B);
    const flash = (t) => { bCopyJson.textContent = t; setTimeout(() => { bCopyJson.textContent = "复制构筑"; }, 1600); };
    try{
      await navigator.clipboard.writeText(txt);
      flash("✓ 构筑已复制");
    }catch(e){
      const ta = document.createElement("textarea");
      ta.value = txt; ta.style.position = "fixed"; ta.style.opacity = "0";
      document.body.appendChild(ta); ta.select();
      try{ document.execCommand("copy"); flash("✓ 构筑已复制"); }
      catch(e2){ flash("复制失败"); }
      ta.remove();
    }
  };
  const bIO = el("button", "pl-btn", ui.showIO ? "收起导入导出" : "导出 / 导入");
  bIO.onclick = () => { ui.showIO = !ui.showIO; renderAll(); if(ui.showIO){ const t = $("#pl-io-text"); if(t) t.focus(); } };
  const selLayout = el("select");
  selLayout.innerHTML = "<option value='share'>排版 · 分享卡</option><option value='card'>排版 · 卡片</option><option value='twocol'>排版 · 双栏</option><option value='minimal'>排版 · 极简</option>";
  selLayout.value = LAYOUT;
  selLayout.title = "分享图的排版风格";
  selLayout.onchange = () => { LAYOUT = selLayout.value; try{ localStorage.setItem("taiwu_planner_layout", LAYOUT); }catch(e){} };
  const bImg = el("button", "pl-btn primary", "复制图片");
  bImg.title = "把当前构筑生成分享图并复制到剪贴板（可直接粘贴）";
  bImg.onclick = () => exportBuildImage();
  bar.append(nameInp, sel, bNew, bCopy, bDel, bCopyJson, bIO, selLayout, bImg);
  return bar;
}
function buildSummaryTag(b){
  const ch = D.characters.find(c => c.id === b.char);
  return (ch ? ch.name : "空") + " · 功法" + b.skills.length + "/装备" + Object.keys(b.equips).length;
}

function renderIO(){
  const p = el("div", "panel");
  p.appendChild(el("div", "sec-title", "导出 / 导入（JSON）"));
  p.appendChild(el("p", "", "<span style='font-size:12px;color:var(--text-faint)'>导出当前构筑的完整 JSON（后续版本将支持链接分享）。粘贴他人分享的 JSON 后点「导入」即可载入。</span>"));
  const ta = el("textarea", "pl-io"); ta.id = "pl-io-text";
  ta.value = JSON.stringify(B);
  const row = el("div", "filters"); row.style.marginTop = "8px";
  const bCopy = el("button", "pl-btn", "复制到剪贴板");
  bCopy.onclick = () => {
    if(!confirmPicksComplete()) return;
    ta.select();
    try{ document.execCommand("copy"); bCopy.textContent = "已复制"; setTimeout(() => bCopy.textContent = "复制到剪贴板", 1200); }catch(e){}
  };
  const bImp = el("button", "pl-btn", "导入文本框内容");
  bImp.onclick = () => {
    try{
      const obj = JSON.parse(ta.value);
      const b = sanitize(obj);
      b.name = (obj && obj.name ? String(obj.name) : "导入的构筑").slice(0, 40);
      store.list.push(b); store.active = store.list.length - 1; B = b;
      ui.showIO = false; save(); renderAll();
    }catch(e){ alert("导入失败：不是有效的构筑 JSON"); }
  };
  row.append(bCopy, bImp);
  p.append(ta, row);
  return p;
}

/* ============ 编辑区 ============ */
function renderEdit(c){
  renderChars(c);
  if(B.char){
    renderTalents(c);
    renderSkills(c);
    renderPets(c);
    renderEquips(c);
    renderElixirs(c);
    renderNote(c);
  }else{
    c.appendChild(el("div", "panel", "<p style='color:var(--text-faint)'>选择角色后开始构筑。</p>"));
  }
}

function renderChars(c){
  c.appendChild(el("div", "sec-title", "① 角色"));
  const g = el("div", "pl-chargrid");
  D.characters.forEach(ch => {
    const card = el("div", "pl-char" + (B.char === ch.id ? " on" : ""),
      (ch.icon ? "<img src='" + imgSrc(ch.icon) + "' alt=''>" : "") +
      "<div><div class='nm'>" + esc(ch.name) + "</div><div class='tt'>" + esc(ch.title || "") + "</div></div>");
    card.onclick = () => {
      if(B.char === ch.id) return;
      B.char = ch.id;
      // 天赋重置：第 0 层绝学自动选，其余清空
      B.talents = ch.talent_tree.map((tier, i) => (i === 0 && tier.length) ? tier[0].id : null);
      save(); renderAll();
    };
    g.appendChild(card);
  });
  c.appendChild(g);
}

function renderTalents(c){
  const ch = charOf();
  c.appendChild(el("div", "sec-title", "② 境界天赋（每层选 1）"));
  ch.talent_tree.forEach((tier, i) => {
    if(!tier.length) return;
    const row = el("div", "pl-talrow");
    row.appendChild(el("div", "pl-tt", i === 0 ? "绝学" : "境界" + i));
    tier.forEach(t => {
      const on = B.talents[i] === t.id;
      const card = el("div", "pl-tal" + (on ? " on" : ""),
        "<div class='nm'>" + esc(t.name || t.id) + "</div><div class='ds'>" + rich(t.desc) + "</div>");
      card.onclick = () => { B.talents[i] = t.id; save(); renderAll(); };
      row.appendChild(card);
    });
    c.appendChild(row);
  });
}

function renderSkills(c){
  c.appendChild(el("div", "sec-title", "③ 功法（上限 " + MAX_SLOTS + " 门 · 顺序即构筑优先级）"));
  const g = el("div", "pl-slotgrid");
  for(let i = 0; i < MAX_SLOTS; i++){
    const slot = el("div", "pl-slot");
    const en = B.skills[i];
    if(!en){
      const empty = el("div", "empty", "＋ 选择功法");
      empty.onclick = () => openSkillModal(i);
      slot.appendChild(empty);
    }else{
      renderSkillSlot(slot, en, i);
    }
    g.appendChild(slot);
  }
  c.appendChild(g);
}

function renderSkillSlot(slot, en, i){
  const s = SKILL_BY_ID.get(en.id);
  const pts = points(en);
  const own = skillOwner(s.id);
  const grad = graduated(en);
  const evo = s.evolutions.find(e => e.id === en.evo) || null;
  const head = el("div", "pl-skhead",
    iconImg(s.icon) +
    "<span class='nm'>槽" + (i + 1) + " · " + esc(s.name) + "</span>" +
    (evo ? "<span class='badge r4'>" + esc(evo.name.replace(s.name + "·", "·")) + "</span>" : "") +
    (own ? "<span class='badge own'>" + esc(own.name) + (own.init ? " · 初始" : "") + "</span>" : "") +
    "<span class='pl-grad" + (grad ? "" : " half") + "'>" + (grad ? "毕业" : "参悟 " + pts + "/" + MAX_POINTS) + "</span>" +
    "<span class='ops'></span>");
  const ops = head.querySelector(".ops");
  if(i > 0){
    const up = el("button", "pl-mini", "▲");
    up.title = "上移";
    up.onclick = () => { const t = B.skills[i - 1]; B.skills[i - 1] = B.skills[i]; B.skills[i] = t; save(); renderAll(); };
    ops.appendChild(up);
  }
  if(i < B.skills.length - 1){
    const dn = el("button", "pl-mini", "▼");
    dn.title = "下移";
    dn.onclick = () => { const t = B.skills[i + 1]; B.skills[i + 1] = B.skills[i]; B.skills[i] = t; save(); renderAll(); };
    ops.appendChild(dn);
  }
  const link = el("button", "pl-mini", "图鉴");
  link.onclick = (ev) => { ev.stopPropagation(); location.hash = "#/skills/" + encodeURIComponent(s.id); };
  ops.appendChild(link);
  const swap = el("button", "pl-mini", "编辑");
  swap.title = "在弹窗里改参悟分支与进化";
  swap.onclick = () => openSkillModal(i);
  ops.appendChild(swap);
  const rm = el("button", "pl-mini", "移除");
  rm.onclick = () => { B.skills.splice(i, 1); save(); renderAll(); };
  ops.appendChild(rm);
  slot.appendChild(head);

  if(evo){
    slot.appendChild(el("div", "pl-evoone",
      "<div class='nm'>进化 · " + esc(evo.name) + (evo.add_tags && evo.add_tags.length ? " " + evo.add_tags.map(t => "<span class='badge x'>" + esc(t) + "</span>").join(" ") : "") + "</div>" +
      "<div class='ds'>" + rich(evo.desc) + "</div>"));
  }
  s.upgrades.forEach((branch, bi) => {
    if(!en.lv[bi]) return;
    slot.appendChild(el("div", "pl-slotbranch",
      (branch[0].icon ? iconImg(branch[0].icon) : "") +
      "<div><div class='nm'>" + esc(branchName(branch)) + " <span class='pl-tip'>L" + en.lv[bi] + " 满级</span></div>" +
      "<div class='ds'>" + rich(branch[en.lv[bi] - 1].desc) + "</div></div>"));
  });
  // 该功法全部可提供的全局收益 / 对敌减益（按分支分组，点满口径，与当前勾选无关）
  const groups = skillGlobalByBranch(s);
  if(groups.length){
    const lines = groups.map(g => {
      const chips = g.items.map(it => {
        const tip = it.txt || "";
        return "<span class='pl-gchip" + (it.depend ? " dep" : "") + (it.cls ? " " + it.cls : "") +
          (tip ? " title='" + esc(tip) + "'" : "") + ">" +
          esc(it.disp) + (it.cond ? "<i>" + esc(it.cond) + "</i>" : "") +
          "<b>" + (it.cls ? "✦" : it.depend ? "动态" : esc(fmtGainVal(it, it.v))) + "</b></span>";
      }).join("");
      return "<div class='pl-gline'><span class='bl'>" + esc(g.label) + "</span>" + chips + "</div>";
    }).join("");
    slot.appendChild(el("div", "pl-slotglobal",
      "<div class='hd'><span class='gl'>全局收益</span>" +
      "<span class='pl-tip'>该功法可提供（含未选分支 · 每条按点满 L1+L2+L3 计）</span></div>" + lines));
  }
  if(pts < MAX_POINTS || !en.evo){
    slot.appendChild(el("div", "pl-tip",
      "未毕业（参悟 " + pts + "/" + MAX_POINTS + (en.evo ? "" : " · 未选进化") + "）· 点「编辑」在弹窗里补选"));
  }
}

/* ---------- 选择器弹窗（master-detail：左列表 + 右完整详情）---------- */
let MODAL = null; // {type:"skill"|"equip", slot, cur}
let LAYOUT = "share";   // 分享图排版：share 分享卡 / card 卡片 / twocol 双栏 / minimal 极简
try{ LAYOUT = localStorage.getItem("taiwu_planner_layout") || "share"; }catch(e){}
if(!["share", "card", "twocol", "minimal"].includes(LAYOUT)) LAYOUT = "share";

function closePlModal(){
  const ov = document.querySelector(".pl-mov");
  if(ov) ov.remove();
  document.body.classList.remove("pl-modal-open");
}
document.addEventListener("keydown", e => {
  if(e.key === "Escape" && document.querySelector(".pl-mov")){ closePlModal(); if(ROOT) renderAll(); }
});
/* 弹窗挂在 body 上，路由离开构筑台时务必收掉 */
window.addEventListener("hashchange", () => {
  if(!location.hash.startsWith("#/planner")) closePlModal();
});
function openModalShell(title){
  closePlModal();
  const ov = el("div", "pl-mov");
  const box = el("div", "pl-modal");
  const head = el("div", "pl-mhead");
  head.appendChild(el("span", "mtitle", esc(title)));
  const close = el("button", "pl-mclose", "✕");
  close.title = "关闭 (Esc)";
  close.onclick = () => { closePlModal(); if(ROOT) renderAll(); };
  const body = el("div", "pl-mbody");
  const list = el("div", "pl-mlist");
  const detail = el("div", "pl-mdetail");
  body.append(list, detail);
  head.appendChild(close);
  box.append(head, body);
  ov.appendChild(box);
  ov.onclick = e => { if(e.target === ov){ closePlModal(); if(ROOT) renderAll(); } };
  document.body.appendChild(ov);
  document.body.classList.add("pl-modal-open");
  return { head, list, detail };
}

function equipOf(slot){
  const v = B.equips[slot];
  if(!v) return null;
  const o = typeof v === "string" ? { id: v, picks: {} } : v;
  return EQUIP_BY_ID.has(o.id) ? o : null;
}
const isFxTrait = t => !!(t && (t.name == null || /<color/i.test(String(t.name))));
 
function equipTotalCount(e){
  const fixed = (e.fixed_traits || []).length;
  const draws = (e.random_trait_pools || []).reduce((a, p) => a + Math.max(0, +p.count || 0), 0);
  return fixed + draws;
}
function equipStructureText(e){
  const fx = (e.fixed_traits || []).filter(isFxTrait).length;
  const pools = (e.random_trait_pools || []);
  const draws = pools.map(p => Math.max(0, +p.count || 0));
  return "共 " + equipTotalCount(e) + " 条：固有 " + (e.fixed_traits || []).length +
    (fx ? "（含特效 " + fx + "）" : "") + " · 随机 " + draws.join(" + ");
}
function fxText(t){
  let raw = (t && t.name) || ((TRAIT_BY_ID.get(t && t.id) || {}).desc) || "";
  if(!raw) return "紫色词条来自随机词条池";
  raw = String(raw)
  return raw.replace(/<[^>]*>/g, "").replace(/\s+/g, " ").trim();
}

/* ---- 功法选择弹窗 ---- */
function openSkillModal(slotIdx, currentId){
  ui.pickerQ = ""; ui.pickerTags = new Set(); ui.pSort = "game";
  const sh = openModalShell("选择功法 · 槽 " + (slotIdx + 1));
  const curEntry = B.skills[slotIdx] || null;
  const curId = currentId || (curEntry ? curEntry.id : null);
  MODAL = { type: "skill", slot: slotIdx, cur: curId,
            sel: (curEntry && curId === curEntry.id)
              ? { br: curEntry.lv.map((n, i) => n > 0 ? i : -1).filter(i => i >= 0), evo: curEntry.evo }
              : { br: [], evo: null } };
  const inp = el("input"); inp.placeholder = "搜索功法名 / 参悟·进化效果（弹射、光环、暴击…）";
  const sort = el("select");
  sort.innerHTML = "<option value='game'>按游戏顺序</option><option value='dmg'>按伤害</option><option value='cd'>按冷却</option><option value='name'>按名称</option>";
  const chips = el("span", "");
  chips.style.cssText = "display:flex;gap:5px;flex-wrap:wrap";
  const allTags = [...new Set(PLAYER_SKILLS.flatMap(s => [...(s.tags || []), ...(s.extra_tags || [])]))]
    .sort((a, b) => { const ia = TAG_ORDER.indexOf(a), ib = TAG_ORDER.indexOf(b); return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib); });
  function paintChips(){
    chips.innerHTML = "";
    allTags.forEach(t => {
      const on = ui.pickerTags.has(t);
      const b = el("span", "badge" + (on ? " r2" : ""), esc(t));
      b.style.cursor = "pointer";
      if(on){ b.style.color = "var(--r-rare)"; b.style.borderColor = "var(--r-rare)"; }
      b.onclick = () => { on ? ui.pickerTags.delete(t) : ui.pickerTags.add(t); paintChips(); renderList(); };
      chips.appendChild(b);
    });
  }
  sh.head.insertBefore(inp, sh.head.lastChild);
  sh.head.insertBefore(sort, sh.head.lastChild);
  sh.head.insertBefore(chips, sh.head.lastChild);

  function skillMatches(s){
    const q = ui.pickerQ.trim().toLowerCase();
    if(q && !skillSearchText(s).includes(q) && !s.name.toLowerCase().includes(q)) return false;
    const tl = [...(s.tags || []), ...(s.extra_tags || [])];
    for(const t of ui.pickerTags) if(!tl.includes(t)) return false;
    return true;
  }
  function sorted(){
    let l = PLAYER_SKILLS.filter(skillMatches);
    if(ui.pSort === "dmg") l = [...l].sort((a, b) => (parseFloat((b.base || {})["伤害"]) || 0) - (parseFloat((a.base || {})["伤害"]) || 0));
    else if(ui.pSort === "cd") l = [...l].sort((a, b) => (parseFloat((a.base || {})["冷却(秒)"]) || 99) - (parseFloat((b.base || {})["冷却(秒)"]) || 99));
    else if(ui.pSort === "name") l = [...l].sort((a, b) => a.name.localeCompare(b.name, "zh"));
    return l;
  }
  function renderList(){
    sh.list.innerHTML = "";
    const usedSet = new Set(B.skills.map(x => x.id));
    const list = sorted();
    if(!list.length){ sh.list.appendChild(el("div", "pl-mempty", "没有匹配的功法")); return; }
    sh.list.appendChild(el("div", "pl-tip", "点左侧功法查看 · 双击（选好进化后）直接加入"));
    list.forEach(s => {
      const own = skillOwner(s.id);
      const isUsed = usedSet.has(s.id);
      const it = el("div", "pl-mitem" + (MODAL.cur === s.id ? " on" : ""),
        iconImg(s.icon) +
        "<div style='min-width:0'><div class='nm'>" + esc(s.name) + (isUsed ? " <span class='badge own'>已装备</span>" : "") + "</div>" +
        "<div class='sub'>" +
        [...(s.tags || []), ...(s.extra_tags || [])].slice(0, 4).map(t => "<span class='badge'>" + esc(t) + "</span>").join(" ") +
        (own ? " <span class='badge own'>" + esc(own.name) + (own.init ? "·初始" : "") + "</span>" : "") +
        "</div></div>" +
        "<div class='right'>" +
        ((s.base || {})["伤害"] ? "伤害 " + esc(s.base["伤害"]) + "<br>" : "") +
        ((s.base || {})["冷却(秒)"] ? "CD " + esc(s.base["冷却(秒)"]) + "s" : "") +
        "</div>");
      it.onclick = () => { MODAL.cur = s.id; renderList(); renderDetail(); };
      it.ondblclick = () => {
        if(!MODAL.sel.evo){ it.classList.add("flash"); setTimeout(() => it.classList.remove("flash"), 400); return; }
        const entry = { id: s.id, evo: MODAL.sel.evo, lv: s.upgrades.map((_, bi) => MODAL.sel.br.includes(bi) ? 3 : 0) };
        if(B.skills.length > MODAL.slot) B.skills[MODAL.slot] = entry; else B.skills.push(entry);
        save(); closePlModal(); renderAll();
      };
      sh.list.appendChild(it);
    });
    const on = sh.list.querySelector(".pl-mitem.on");
    if(on) on.scrollIntoView({ block: "nearest" });
  }
  function renderDetail(){
    sh.detail.innerHTML = "";
    const s = MODAL.cur ? SKILL_BY_ID.get(MODAL.cur) : null;
    if(!s){ sh.detail.appendChild(el("div", "pl-mempty", "在左侧选择一门功法，这里显示完整资料")); return; }
    const own = skillOwner(s.id);
    sh.detail.appendChild(el("div", "pl-md-hd",
      iconImg(s.icon) +
      "<div><div class='nm'>" + esc(s.name) +
      (own ? " <span class='badge own'>" + esc(own.name) + (own.init ? " · 初始" : "") + "</span>" : "") + "</div>" +
      "<div class='row'>" +
      (s.tags || []).map(t => "<span class='badge'>" + esc(t) + "</span>").join("") +
      (s.extra_tags || []).map(t => "<span class='badge x'>" + esc(t) + "</span>").join("") +
      "</div>" +
      "<div class='row'>" + Object.entries(s.base || {}).filter(([, v]) => v != null && v !== "").map(([k, v]) =>
        "<span class='badge r1'>" + esc(k) + " " + esc(v) + "</span>").join("") + "</div>" +
      "</div>"));
    sh.detail.appendChild(el("div", "pl-mdsec", "功法描述"));
    sh.detail.appendChild(el("div", "pl-mdesc", rich(s.desc)));

    const foot = el("div", "pl-mfoot");
    const status = el("span", "pl-status");
    const linkBtn = el("button", "pl-btn", "在图鉴中查看");
    linkBtn.onclick = () => { closePlModal(); location.hash = "#/skills/" + encodeURIComponent(s.id); };
    const isElse = !B.skills.some((x, xi) => xi !== MODAL.slot && x.id === s.id);
    const okBtn = el("button", "pl-btn primary", "");
    function paintStatus(){
      const n = MODAL.sel.br.length;
      status.innerHTML = "分支 <b>" + n + "/3</b>（每条满级 L3）· " +
        (MODAL.sel.evo ? "进化 <b>已选</b>" : "进化 <b style='color:var(--red)'>未选</b>");
      const here = B.skills[MODAL.slot];
      if(!isElse){ okBtn.textContent = "已在其它槽位"; okBtn.style.opacity = ".45"; return; }
      if(!MODAL.sel.evo){ okBtn.textContent = "请先选进化"; okBtn.style.opacity = ".45"; return; }
      okBtn.style.opacity = "";
      okBtn.textContent = (here && here.id === s.id ? "更新槽 " : "加入槽 ") + (MODAL.slot + 1);
    }
    okBtn.onclick = () => {
      if(!isElse || !MODAL.sel.evo) return;
      const entry = { id: s.id, evo: MODAL.sel.evo,
                      lv: s.upgrades.map((_, bi) => MODAL.sel.br.includes(bi) ? 3 : 0) };
      if(B.skills.length > MODAL.slot) B.skills[MODAL.slot] = entry;
      else B.skills.push(entry);
      save(); closePlModal(); renderAll();
    };

    sh.detail.appendChild(el("div", "pl-mdsec", "参悟分支（五选三 · 点卡片勾选，每条自动满级 L3）"));
    s.upgrades.forEach((branch, bi) => {
      const bd = el("div", "pl-ubranch open pick" + (MODAL.sel.br.includes(bi) ? " on" : ""));
      const bh = el("div", "bh",
        (branch[0].icon ? iconImg(branch[0].icon) : "") +
        "<span class='bn'>" + esc(branchName(branch)) + "</span>" +
        "<span class='selmark'>✓ 已选</span>");
      const bc = el("div", "bc",
        branch.map((u, li) =>
          "<div class='pl-ulv'><span class='lv'>L" + (li + 1) + "</span><div>" + rich(u.desc) + "</div></div>").join(""));
      bd.append(bh, bc);
      bd.onclick = () => {
        const at = MODAL.sel.br.indexOf(bi);
        if(at >= 0) MODAL.sel.br.splice(at, 1);
        else if(MODAL.sel.br.length >= 3){
          bd.classList.add("flash"); setTimeout(() => bd.classList.remove("flash"), 400);
          paintStatus(); return;
        }
        else MODAL.sel.br.push(bi);
        bd.classList.toggle("on", MODAL.sel.br.includes(bi));
        paintStatus();
      };
      sh.detail.appendChild(bd);
    });
    sh.detail.appendChild(el("div", "pl-mdsec", "进化（三选一 · 必选）"));
    s.evolutions.forEach(e => {
      const card = el("div", "pl-evocard pick" + (MODAL.sel.evo === e.id ? " on" : ""),
        "<span class='pickmark'>✓</span>" +
        "<div class='nm'>" + esc(e.name) + (e.add_tags && e.add_tags.length ? " " + e.add_tags.map(t => "<span class='badge x'>" + esc(t) + "</span>").join(" ") : "") + "</div>" +
        "<div class='ds'>" + rich(e.desc) + "</div>" +
        (e.desc2 ? "<div class='ds' style='margin-top:4px;color:var(--text-faint)'>进化后：" + rich(e.desc2) + "</div>" : ""));
      card.onclick = () => {
        MODAL.sel.evo = e.id;
        sh.detail.querySelectorAll(".pl-evocard").forEach(c => c.classList.remove("on"));
        card.classList.add("on");
        paintStatus();
      };
      sh.detail.appendChild(card);
    });
    foot.append(status, linkBtn, okBtn);
    sh.detail.appendChild(foot);
    paintStatus();
  }
  inp.oninput = () => { ui.pickerQ = inp.value; renderList(); };
  sort.onchange = () => { ui.pSort = sort.value; renderList(); };
  paintChips();
  renderList();
  renderDetail();
  inp.focus();
}

function renderPets(c){
  c.appendChild(el("div", "sec-title", "④ 灵侍 / 宛渠之民（随行 1 位 · 点击选择）"));
  const g = el("div", "pl-cellgrid");
  const pet = B.pet ? D.pets.find(p => p.id === B.pet) : null;
  if(!pet){
    const cell = el("div", "pl-cell");
    const empty = el("div", "empty", "＋ 选择灵侍 / 宛渠之民");
    empty.onclick = () => openPetModal();
    cell.appendChild(empty);
    g.appendChild(cell);
  }else{
    const cell = el("div", "pl-cell on");
    cell.appendChild(el("div", "hd",
      (pet.icon ? "<img src='" + imgSrc(pet.icon) + "' alt=''>" : "") +
      "<span class='nm'>" + esc(pet.name) + "</span>" +
      "<span class='badge " + (pet.kind === "宛渠之民" ? "r4" : "r2") + "'>" + esc(pet.kind) + "</span>" +
      (pet.species && pet.species !== "特殊" ? "<span class='badge'>" + esc(pet.species) + "</span>" : "")));
    const pv = petPassiveText(pet);
    cell.appendChild(el("div", "ds", pv || (pet.desc || "").replace(/<[^>]*>/g, "").slice(0, 70)));
    cell.onclick = () => openPetModal();
    const rm = el("button", "pl-mini", "移除");
    rm.style.marginTop = "6px";
    rm.onclick = (ev) => { ev.stopPropagation(); B.pet = null; save(); renderAll(); };
    cell.appendChild(rm);
    g.appendChild(cell);
  }
  c.appendChild(g);
}

function petPassiveText(pet){
  const ps = (pet.passive || []).map(x => {
    let v = x.value;
    if(v != null){
      const n = parseFloat(v);
      if(!isNaN(n) && Math.abs(n) < 1 && n !== 0) v = "+" + Math.round(n * 100) + "%";
      else v = "+" + v;
    }
    return x.stat + " " + (v || "");
  });
  if(ps.length) return "主人被动：" + ps.join("，");
  if(pet.passive_formula) return "主人被动：" + pet.passive_formula;
  return "";
}

/* ---- 灵侍弹窗：单栏全量列表 ---- */
function openPetModal(){
  const sh = openModalShell("灵侍 / 宛渠之民 · 随行 1 位");
  document.querySelector(".pl-modal").classList.add("single");
  sh.detail.remove();
  sh.list.style.borderRight = "none";
  sh.list.style.padding = "12px 16px";
  function render(){
    sh.list.innerHTML = "";
    D.pets.forEach(pt => {
      const on = B.pet === pt.id;
      const row = el("div", "pl-erow" + (on ? " on" : ""));
      if(pt.icon) row.innerHTML = "<img src='" + imgSrc(pt.icon) + "' alt=''>";
      const mid = el("div", "mid");
      mid.appendChild(el("div", "nm",
        esc(pt.name) +
        " <span class='badge " + (pt.kind === "宛渠之民" ? "r4" : "r2") + "'>" + esc(pt.kind) + "</span>" +
        (pt.species && pt.species !== "特殊" ? " <span class='badge'>" + esc(pt.species) + "</span>" : "") +
        (on ? " <span class='badge own'>已选</span>" : "")));
      const facts = ["生命 " + (pt.hp || "?"), "移速 " + (pt.move_speed || "?"), "暴击 " + (pt.crit != null ? Math.round(pt.crit * 100) + "%" : "?")];
      mid.appendChild(el("div", "kvline", facts.join(" · ")));
      const pv = petPassiveText(pt);
      if(pv) mid.appendChild(el("div", "ds", pv));
      if(pt.desc) mid.appendChild(el("div", "ds", rich(pt.desc)));
      if(pt.skills && pt.skills.length){
        const sk = el("div", "ds");
        sk.innerHTML = "<span style='color:var(--gold-dim)'>技能：</span>" +
          pt.skills.map(s => esc(s.name)).join(" · ");
        mid.appendChild(sk);
      }
      if(pt.obtain) mid.appendChild(el("div", "obtain", esc(pt.obtain)));
      const btn = el("button", "pl-btn" + (on ? "" : " primary"), on ? "移除" : "携带");
      if(on){ btn.style.borderColor = "var(--red)"; btn.style.color = "var(--red)"; }
      btn.onclick = () => {
        B.pet = on ? null : pt.id;
        save(); render();
      };
      row.append(mid, btn);
      sh.list.appendChild(row);
    });
  }
  render();
}

/* ---- 装备区与装备选择弹窗 ---- */
const RAR_CLS = { "凡品": "r1", "奇品": "r2", "极品": "r3", "绝品": "r4" };

function renderEquips(c){
  c.appendChild(el("div", "sec-title", "⑤ 装备（9 部位 · 宝物可装 2 件 · 选装备时可直接勾选目标词条）"));
  const p = el("div", "panel"); p.style.padding = "6px 16px";
  SLOT_ORDER.forEach(slot => {
    const row = el("div", "pl-eqrow");
    row.appendChild(el("div", "st", esc(slot)));
    const right = el("div");
    const o = equipOf(slot);
    if(!o){
      const btn = el("button", "pl-btn", "选择装备");
      btn.onclick = () => openEquipModal(slot);
      right.appendChild(btn);
    }else{
      const e = EQUIP_BY_ID.get(o.id);
      const card = el("div", "pl-eqcard",
        "<img src='" + imgSrc(e.icon) + "' alt=''>" +
        "<span class='pl-eqname'>" + esc(e.name) + "</span>" +
        "<span class='badge " + (RAR_CLS[e.rarity] || "") + "'>" + esc(e.rarity) + "</span>");
      card.style.cursor = "pointer";
      card.title = "点击查看完整词条 / 更换";
      card.onclick = ev => { if(ev.target.tagName !== "BUTTON") openEquipModal(slot); };
      const btnLook = el("button", "pl-mini", "词条 / 更换");
      btnLook.onclick = () => openEquipModal(slot);
      const btnClear = el("button", "pl-mini", "清除");
      btnClear.onclick = () => { delete B.equips[slot]; save(); renderAll(); };
      card.append(btnLook, btnClear);
      right.appendChild(card);
      const fixed = (e.fixed_traits || []).map(t => equipTraitLine(t)).filter(Boolean).join("<br>");
      if(fixed) right.appendChild(el("div", "pl-eqinfo", fixed));
      const picked = [];
      (e.random_trait_pools || []).forEach((pool, pi) => {
        ((o.picks || {})[pi] || []).forEach(t => picked.push(t));
      });
      if(picked.length){
        right.appendChild(el("div", "pl-tchips",
          "<span class='pl-tip'>目标词条：</span>" +
          picked.map(t => "<span class='pl-tchip on'>" + rich(t) + "</span>").join("")));
      }
    }
    row.appendChild(right);
    p.appendChild(row);
  });
  c.appendChild(p);

  // 已选装备加成总览：属性合计表 + 固有词条 + 已勾选目标词条
  const pickedSlots = SLOT_ORDER.filter(s => equipOf(s));
  if(pickedSlots.length){
    const ov = el("div", "panel");
    ov.appendChild(el("div", "sec-title",
      "构筑加成总览（装备 " + pickedSlots.length + "/10 · 含天赋与功法参悟 · ✦ 固有特效 · 金色 = 目标词条）"));

    // 属性数值合计：装备词条（区间上限）+ 人物天赋 + 功法参悟（buff deltas）按属性聚合；特效不计入
    const agg = new Map();   // key -> {disp,pct,n,v,src:Map(来源名->贡献值)}
    const addValue = (disp, val, pct, src) => {
      const key = disp + (pct ? "\t%" : "");
      let a = agg.get(key);
      if(!a){ a = { disp, pct, n: 0, v: 0, src: new Map() }; agg.set(key, a); }
      a.v += val; a.n++;
      a.src.set(src, (a.src.get(src) || 0) + val);   // 同来源多次贡献合并
    };
    const addLine = (line, e) => {
      const st = plTraitStat(line);
      if(!st) return;
      addValue(st.key.replace("\t%", ""), st.r[1][1], st.pct, e.name);   // 二阶区间上限
    };
    const addDeltas = (deltas, src) => {
      (deltas || []).forEach(d => {
        if(d.scope === "self") return;   // 仅本功法的面板属性不属于全局合计
        if(d.depend) return;             // 动态公式类：静态原值≠实际生效值，不计入
        const v = parseFloat(d.value);
        if(isNaN(v)) return;
        addValue(d.stat, plIsPctKey(d.key) ? v * 100 : v, plIsPctKey(d.key), src);
      });
    };
    pickedSlots.forEach(s => plCollectEquipLines(EQUIP_BY_ID.get(equipOf(s).id), equipOf(s), addLine));
    // 人物天赋（境界天赋树）
    (B.talents || []).forEach(tid => {
      const t = TALENT_BY_ID.get(tid);
      if(t) addDeltas(t.deltas, "天赋·" + t.name);
    });
    // 功法：本体常驻 buff + 已点参悟分支（取所点等级的等级差量）+ 进化
    (B.skills || []).forEach(en => {
      const s = SKILL_BY_ID.get(en.id);
      if(!s) return;
      const nm = "功法·" + s.name;
      addDeltas(s.base_deltas, nm);
      (s.upgrades || []).forEach((branch, bi) => {
        const lv = (en.lv || [])[bi] || 0;
        for(let li = 0; li < lv; li++) addDeltas(branch[li].deltas, nm);   // 同线 L1..Lv 全部叠加
      });
      if(en.evo){
        const e = (s.evolutions || []).find(x => x.id === en.evo);
        if(e) addDeltas(e.deltas, nm);
      }
    });
    if(agg.size){
      const rows = [...agg.entries()].sort((x, y) => y[1].n - x[1].n || x[1].disp.localeCompare(y[1].disp));
      const tbl = el("div", "pl-statsum");
      tbl.innerHTML =
        "<div class='pl-shd'><span>属性合计（装备+天赋+功法）</span><span>二阶最大值合计</span><span>条数▾</span></div>" +
        rows.map(([k, a]) => {
          const srcRows = [...a.src.entries()]
            .sort((x, y) => y[1] - x[1])
            .map(([nm, v]) =>
              "<div class='pl-srow sub'>" +
              "<span class='snm'>" + esc(nm) + "</span>" +
              "<span class='sv'>" + esc(plFmtSum([v, v], a.pct)) + "</span>" +
              "<span class='sn'></span></div>").join("");
          return "<div class='pl-srow' data-k='" + esc(k) + "'>" +
            "<span class='snm' title='" + esc("来源：" + [...a.src.keys()].join("、")) + "'>" + esc(a.disp) + "</span>" +
            "<span class='sv'>" + esc(plFmtSum([a.v, a.v], a.pct)) + "</span>" +
            "<span class='sn tg'>" + a.n + "</span></div>" + srcRows;
        }).join("");
      tbl.addEventListener("click", ev => {
        const tg = ev.target.closest(".sn.tg");
        if(!tg) return;
        const row = tg.closest(".pl-srow");
        row.classList.toggle("open");
        const on = row.classList.contains("open");
        let nxt = row.nextElementSibling;
        while(nxt && nxt.classList.contains("sub")){
          nxt.style.display = on ? "contents" : "none";
          nxt = nxt.nextElementSibling;
        }
      });
      ov.appendChild(tbl);
      ov.appendChild(el("div", "pl-tip",
        "装备词条取二阶（当前版本最高阶）区间上限（5~8 取 8，实际掉落在区间内随机）；天赋/功法参悟按 buff 常驻面板属性计入（同线 L1~L3 叠加求和）；「仅本功法」与动态公式类（如素女天音·分支三按范围减半算暴击）不计入合计，见功法卡与弹窗明细"));
    }

    // 增益/妨害满层表：只列这套构筑实际能施加的状态——
    // 扫描选中功法（本体/所点分支/进化）+ 天赋 + 装备固有特效的 buff 链，与命名状态表取交集
    const enhanceOf = key => { const a = agg.get(key + "\t%"); return a ? a.v : 0; };
    const castSet = new Set();
    (B.skills || []).forEach(en => {
      const s = SKILL_BY_ID.get(en.id);
      if(!s) return;
      (s.attach_buffs || []).forEach(x => castSet.add(x));
      (s.cast_buffs || []).forEach(x => castSet.add(x));
      (s.upgrades || []).forEach((branch, bi) => {
        const lv = (en.lv || [])[bi] || 0;
        if(lv > 0 && branch[lv - 1]) (branch[lv - 1].add_buffs || []).forEach(x => castSet.add(x));
      });
      if(en.evo){
        const e = (s.evolutions || []).find(x => x.id === en.evo);
        if(e) (e.add_buffs || []).forEach(x => castSet.add(x));
      }
    });
    (B.talents || []).forEach(tid => {
      const t = TALENT_BY_ID.get(tid);
      if(t) (t.buffs || []).forEach(x => castSet.add(x));
    });
    pickedSlots.forEach(s => {
      (EQUIP_BY_ID.get(equipOf(s).id).fixed_traits || []).forEach(t => {
        String(t.buff || "").split(";").filter(Boolean).forEach(x => castSet.add(x));
      });
    });
    const buffSeen = new Set();
    const namedBuffs = tag0 => D.buffs.filter(b =>
      (b.tags || [])[0] === tag0 && +b.max_stack >= 1 && (b.deltas || []).length && b.name
      && castSet.has(b.id)
      && !buffSeen.has(b.name) && (buffSeen.add(b.name), true));   // 龙珠同名变体去重
    const buffs1 = namedBuffs("1"), curses = namedBuffs("3");
    if(buffs1.length || curses.length){
      const all = buffs1.concat(curses);
      const maxCap = Math.max(...all.map(b => +b.max_stack));
      const wantLayers = Math.max(0, Math.min(ui.buffLayers == null ? maxCap : ui.buffLayers, maxCap));
      const block = (list, title, tagTxt, enhKey) => {
        const enh = enhanceOf(enhKey);
        const fmt = (v, pct) => pct ? plFmtSum([v * 100, v * 100], true) : plFmtSum([v, v], false);
        const rows = list.map(b => {
          const lay = Math.min(wantLayers, +b.max_stack);
          const cells = b.deltas.map(d => {
            const pct = plIsPctKey(d.key);
            const per = parseFloat(d.value);
            const raw = per * lay;
            const fin = raw * (1 + enh / 100);
            const up = fin - raw;
            const sign = up >= 0 ? "+" : "";
            return "<div class='bstat3' title='" + esc(fmt(per, pct) + "/层 × " + lay + "层 × (1+" +
              plFmtSum([enh, enh], true) + ")") + "'>" +
              "<span class='s-nm'>" + esc(d.stat) + "</span>" +
              "<span class='s-raw'>" + esc(fmt(raw, pct)) + "</span>" +
              "<span class='s-fin'>" + esc(fmt(fin, pct)) + "</span>" +
              "<span class='s-up'>" + sign + esc(fmt(up, pct)) + "</span></div>";
          }).join("");
          return "<div class='brow'>" +
            "<span class='bnm'>" + esc(b.name) + "</span>" +
            "<span class='blayn'>" + lay + "<span class='text-faint'>/" + (+b.max_stack) + "层</span></span>" +
            "<div class='bstats'>" + cells + "</div></div>";
        }).join("");
        return "<div class='bhd'>" + title + " <span class='btag'>" + tagTxt + " <b>" +
          esc(plFmtSum([enhanceOf(enhKey), enhanceOf(enhKey)], true)) + "</b></span></div>" +
          "<div class='bstat3 bh3'><span></span><span>原始</span><span>最终</span><span>提升</span></div>" + rows;
      };
      const bc = el("div", "pl-buffcalc");
      bc.innerHTML =
        (buffs1.length ? block(buffs1, "增益满层属性", "吃「增益效果增强」", "增益效果增强") : "") +
        (curses.length ? block(curses, "妨害满层属性（作用于敌人）", "吃「妨害效果增强」", "妨害效果增强") : "") +
        "<div class='bmeta'>效果增强自动取自构筑合计 · 层数 <input type='number' min='0' max='" + maxCap +
        "' value='" + wantLayers + "'>（统一调节，超出按各状态上限截断）</div>";
      const li = bc.querySelector("input");
      li.onchange = () => {
        ui.buffLayers = Math.max(0, Math.min(+li.value, maxCap));
        renderAll();
      };
      // 两列布局：左合计表 + 右状态满层表
      const wrap = el("div", "pl-ov2col");
      const left = el("div");
      left.append(...ov.querySelectorAll(".pl-statsum, .pl-statsum ~ .pl-tip"));
      wrap.append(left, bc);
      ov.appendChild(wrap);
    }
    pickedSlots.forEach(s => {
      const o = equipOf(s);
      const e = EQUIP_BY_ID.get(o.id);
      const picked = [];
      (e.random_trait_pools || []).forEach((pool, pi) => {
        ((o.picks || {})[pi] || []).forEach(t => picked.push(t));
      });
      const row = el("div", "pl-ovrow");
      row.innerHTML =
        "<div class='ovhd'>" + "<img class='ovicon' src='" + imgSrc(e.icon) + "' alt=''>" +
        "<span class='pl-eqname'>" + esc(e.name) + "</span>" +
        "<span class='badge " + (RAR_CLS[e.rarity] || "") + "'>" + esc(e.rarity) + "</span>" +
        "<span class='ovslot'>" + esc(s) + "</span></div>" +
        "<div class='ovlist'>" +
        ((e.fixed_traits || []).map(t => equipTraitLine(t)).filter(Boolean).join("<br>") || "") +
        (picked.length ? (e.fixed_traits && e.fixed_traits.length ? "<br>" : "") +
          picked.map(t => "<span style='color:var(--gold)'>◎ " + rich(t) + "</span>").join("<br>") : "") +
        (!e.fixed_traits.length && !picked.length ? "<span style='color:var(--text-faint)'>（无固有词条，未勾目标词条）</span>" : "") +
        "</div>";
      ov.appendChild(row);
    });
    c.appendChild(ov);
  }
}

function openEquipModal(slot){
  ui.eqQ = ""; ui.eqRar = "";
  const sh = openModalShell("选择装备 · " + slot);
  const prev = equipOf(slot);
  MODAL = { type: "equip", slot, cur: prev ? prev.id : null,
            picks: prev ? JSON.parse(JSON.stringify(prev.picks || {})) : {} };
  const inp = el("input"); inp.placeholder = "搜索装备名 / 固有词条…";
  const rar = el("select");
  rar.innerHTML = "<option value=''>全部品阶</option>" + ["凡品", "奇品", "极品", "绝品"].map(r => "<option>" + r + "</option>").join("");
  sh.head.insertBefore(inp, sh.head.lastChild);
  sh.head.insertBefore(rar, sh.head.lastChild);

  const items = CLEAN_EQUIP.filter(e => e.slot === SLOT_CAT(slot))
    .sort((a, b) => (RAR_ORDER[b.rarity] || 0) - (RAR_ORDER[a.rarity] || 0) || a.name.localeCompare(b.name, "zh"));
  const eqText = e => (e.name + " " + (e.desc || "") + " " +
    (e.fixed_traits || []).map(t => (t.name || "") + " " + (TRAIT_BY_ID.get(t.id) || {}).desc || "")).toLowerCase();

  function renderList(){
    sh.list.innerHTML = "";
    const q = ui.eqQ.trim().toLowerCase();
    const list = items.filter(e => (!ui.eqRar || e.rarity === ui.eqRar) &&
      (!q || eqText(e).includes(q)));
    if(!list.length){ sh.list.appendChild(el("div", "pl-mempty", "没有匹配的装备")); return; }
    sh.list.appendChild(el("div", "pl-tip", "点左侧装备查看并替换 · 双击直接装备"));
    list.forEach(e => {
      const fx = (e.fixed_traits || []).filter(isFxTrait).map(fxText).filter(Boolean);
      const it = el("div", "pl-mitem" + (MODAL.cur === e.id ? " on" : ""),
        "<img src='" + imgSrc(e.icon) + "' alt=''>" +
        "<div style='min-width:0;flex:1'><div class='nm'>" + esc(e.name) +
        (equipOf(slot) && equipOf(slot).id === e.id ? " <span class='badge own'>当前</span>" : "") + "</div>" +
        (fx.length ? "<div class='fxline'><span class='fmark'>✦ </span>" + esc(fx.join(" / ")) + "</div>" : "") +
        "</div>" +
        "<div class='right'><span class='badge " + (RAR_CLS[e.rarity] || "") + "'>" + esc(e.rarity) + "</span><br>" +
        "<span style='color:var(--text-faint);font-size:11px'>" + esc(equipTotalCount(e) + " 条词条") + "</span></div>");
      it.onclick = () => { MODAL.cur = e.id; MODAL.picks = {}; renderList(); renderDetail(); };
      it.ondblclick = () => { B.equips[slot] = { id: e.id, picks: {} }; save(); closePlModal(); renderAll(); };
      sh.list.appendChild(it);
    });
    const on = sh.list.querySelector(".pl-mitem.on");
    if(on) on.scrollIntoView({ block: "nearest" });
  }
  function renderDetail(){
    sh.detail.innerHTML = "";
    const e = MODAL.cur ? EQUIP_BY_ID.get(MODAL.cur) : null;
    if(!e){ sh.detail.appendChild(el("div", "pl-mempty", "在左侧选择一件装备，这里显示固有词条与随机词条池")); return; }
    sh.detail.appendChild(el("div", "pl-md-hd",
      "<img src='" + imgSrc(e.icon) + "' alt=''>" +
      "<div><div class='nm'>" + esc(e.name) + " <span class='badge " + (RAR_CLS[e.rarity] || "") + "'>" + esc(e.rarity) + "</span>" +
      " <span class='badge'>" + esc(SLOT_CAT(slot)) + "</span></div>" +
      "<div class='row'><span class='badge r1'>" + esc(equipStructureText(e)) + "</span></div>" +
      "<div class='row'>基准价 " + esc(e.price || "—") + " · " + esc(e.name_en || "") + "</div></div>"));
    if(e.desc) sh.detail.appendChild(el("div", "pl-mdesc", rich(e.desc)));
    sh.detail.appendChild(el("div", "pl-mdsec", "固有词条（每件固定 · ✦ 为特效）"));
    const fixed = (e.fixed_traits || []).map(t => equipTraitLine(t)).filter(Boolean).join("<br>");
    sh.detail.appendChild(el("div", "pl-mdesc", fixed || "<span style='color:var(--text-faint)'>无</span>"));
    sh.detail.appendChild(el("div", "pl-mdsec", "随机词条池（点选你的目标词条 · 掉落与铁匠重锻都在本池内 roll，已有词条会被排除；数值区间随品阶档位提升）"));

    const status = el("span", "pl-status");
    const okBtn = el("button", "pl-btn primary", "");
    function paintStatus(){
      let n = 0, cap = 0;
      (e.random_trait_pools || []).forEach(pool => { cap += Math.max(0, +pool.count || 0); });
      Object.keys(MODAL.picks).forEach(pi => { n += (MODAL.picks[pi] || []).length; });
      status.innerHTML = "目标词条 <b>" + n + "/" + cap + "</b>（可不选满）";
      const cur2 = equipOf(slot);
      okBtn.textContent = (cur2 && cur2.id === e.id) ? "保存词条选择" : "✓ 装备这件（替换）";
    }
    (e.random_trait_pools || []).forEach((pool, pi) => {
      const cap = Math.max(0, +pool.count || 0);
      const box = el("div");
      box.style.marginBottom = "16px";
      if(cap === 0){
        box.appendChild(el("div", "pl-tip", "第 " + (pi + 1) + " 条 · 该池抽取数为 0，不参与掉落（仅列出参考）："));
        box.appendChild(el("div", "pl-tchips",
          (pool.traits || []).map(t => "<span class='pl-tchip' style='opacity:.4;cursor:default'>" + rich(t) + "</span>").join("")));
        sh.detail.appendChild(box);
        return;
      }
      box.appendChild(el("div", "pl-tip", "第 " + (pi + 1) + " 条 · 池内 " + (pool.traits || []).length + " 选 " + cap + "："));
      const chips = el("div", "pl-tchips");
      (pool.traits || []).forEach(t => {
        const sel = ((MODAL.picks[pi] || []).includes(t));
        const chip = el("span", "pl-tchip" + (sel ? " on" : ""), rich(t));
        chip.onclick = () => {
          const arr = MODAL.picks[pi] = MODAL.picks[pi] || [];
          const at = arr.indexOf(t);
          if(at >= 0) arr.splice(at, 1);
          else if(arr.length >= cap){ chip.classList.add("flash"); setTimeout(() => chip.classList.remove("flash"), 400); paintStatus(); return; }
          else arr.push(t);
          chip.classList.toggle("on", arr.includes(t));
          paintStatus();
        };
        chips.appendChild(chip);
      });
      box.appendChild(chips);
      sh.detail.appendChild(box);
    });
    const foot = el("div", "pl-mfoot");
    const linkBtn = el("button", "pl-btn", "在装备页查看");
    linkBtn.onclick = () => { closePlModal(); location.hash = "#/equipment/" + encodeURIComponent(e.id); };
    okBtn.onclick = () => {
      B.equips[slot] = { id: e.id, picks: MODAL.picks };
      save(); closePlModal(); renderAll();
    };
    foot.append(status, linkBtn, okBtn);
    sh.detail.appendChild(foot);
    paintStatus();
  }
  inp.oninput = () => { ui.eqQ = inp.value; renderList(); };
  rar.onchange = () => { ui.eqRar = rar.value; renderList(); };
  renderList();
  renderDetail();
  inp.focus();
}

function renderElixirs(c){
  c.appendChild(el("div", "sec-title", "⑥ 丹药（完成任务获得 · 一局约 12 分钟只有 2 个任务 → 2 个格子 · 点击选择）"));
  const g = el("div", "pl-cellgrid");
  const cells = Math.max(2, B.elixirs.length);
  for(let i = 0; i < cells; i++){
    const id = B.elixirs[i];
    const x = id ? ELIXIR_BY_ID.get(String(id)) : null;
    const cell = el("div", "pl-cell" + (x ? " on" : ""));
    if(!x){
      const empty = el("div", "empty", "＋ 选择丹药");
      empty.onclick = () => openElixirModal();
      cell.appendChild(empty);
    }else{
      cell.appendChild(el("div", "hd",
        (x.icon ? iconImg(x.icon, "icon-sm") : "") +
        "<span class='nm'>" + esc(x.name) + "</span>" +
        (x.droppable ? "" : " <span class='badge' style='color:var(--text-faint);border-color:var(--line2)'>暂不可获取</span>")));
      cell.appendChild(el("div", "ds", (x.desc || "").replace(/<[^>]*>/g, "").slice(0, 80)));
      cell.onclick = () => openElixirModal();
    }
    g.appendChild(cell);
  }
  c.appendChild(g);
  const n = B.elixirs.length;
  const stat = el("div", "pl-tip");
  stat.style.margin = "8px 0 0";
  stat.innerHTML = n === 0
    ? "每颗丹药都要靠完成任务获取，局内不可带回。"
    : "已选 <b style='color:var(--gold)'>" + n + "</b> 颗" +
      (n > 2 ? " <b style='color:var(--red)'>—— 超出一局任务上限（2），多出的只能指望地图掉落</b>" : " / 2（一局任务数）");
  c.appendChild(stat);
}

/* ---- 丹药弹窗：单栏全量列表，一屏看全部效果 ---- */
function openElixirModal(){
  const sh = openModalShell("丹药全表（完成任务获得 · 一局约 2 颗 · 局内被动不可带回）");
  document.querySelector(".pl-modal").classList.add("single");   // 单栏布局
  sh.detail.remove();
  sh.list.style.borderRight = "none";
  sh.list.style.padding = "12px 16px";
  const inp = el("input"); inp.placeholder = "搜索丹药 / 效果…";
  sh.head.insertBefore(inp, sh.head.lastChild);
  function render(){
    sh.list.innerHTML = "";
    const q = inp.value.trim().toLowerCase();
    const list = D.elixirs.filter(x => !q ||
      x.name.toLowerCase().includes(q) ||
      (x.desc || "").replace(/<[^>]*>/g, "").toLowerCase().includes(q));
    if(!list.length){ sh.list.appendChild(el("div", "pl-mempty", "没有匹配的丹药")); return; }
    list.forEach(x => {
      const on = B.elixirs.includes(x.id);
      const row = el("div", "pl-erow" + (on ? " on" : ""));
      const mid = el("div", "mid",
        "<div class='nm'>" + esc(x.name) +
        (on ? " <span class='badge own'>已选</span>" : "") +
        (x.droppable ? "" : " <span class='badge' style='color:var(--text-faint);border-color:var(--line2)'>暂不可获取</span>") +
        "</div>" +
        "<div class='ds'>" + (rich(x.desc) || "（无描述）") + "</div>");
      const btn = el("button", "pl-btn" + (on ? "" : " primary"), on ? "移除" : "加入");
      if(on){ btn.style.borderColor = "var(--red)"; btn.style.color = "var(--red)"; }
      btn.onclick = () => {
        const i = B.elixirs.indexOf(x.id);
        if(i >= 0) B.elixirs.splice(i, 1);
        else B.elixirs.push(x.id);
        save(); render();
      };
      if(x.icon) row.innerHTML = iconImg(x.icon);
      row.append(mid, btn);
      sh.list.appendChild(row);
    });
    const st = el("div", "pl-status");
    st.style.cssText = "padding:10px 4px 2px;text-align:center";
    const n = B.elixirs.length;
    st.innerHTML = "已选 <b>" + n + "</b>" + (n > 2 ? " <b style='color:var(--red)'>—— 超出一局任务上限（2）</b>" : " / 2（一局任务数）");
    sh.list.appendChild(st);
  }
  inp.oninput = () => render();
  render();
  inp.focus();
}

function renderNote(c){
  c.appendChild(el("div", "sec-title", "⑦ 构筑思路（可选）"));
  const ta = el("textarea", "pl-note");
  ta.placeholder = "输出循环、优劣势、成型条件……";
  ta.value = B.note;
  ta.oninput = () => { B.note = ta.value; save(); };
  c.appendChild(ta);
}

/* ============ 汇总面板 ============ */
function renderSide(c){
  const checks = el("div", "panel");
  checks.appendChild(el("div", "sec-title", "构筑检查"));
  const ch = charOf();
  addCheck(checks, "角色", ch ? ch.name : "未选择", !!ch);
  if(ch){
    for(let i = 0; i < ch.talent_tree.length; i++){
      if(!ch.talent_tree[i].length) continue;
      addCheck(checks, i === 0 ? "绝学" : "境界" + i,
        B.talents[i] ? talentName(ch, i, B.talents[i]) : "未选", !!B.talents[i]);
    }
  }
  addCheck(checks, "功法", B.skills.length + " / " + MAX_SLOTS, B.skills.length > 0);
  B.skills.forEach((en, i) => {
    const s = SKILL_BY_ID.get(en.id);
    addCheck(checks, "　槽" + (i + 1) + " " + s.name,
      graduated(en) ? "毕业" : "参悟 " + points(en) + "/" + MAX_POINTS + (en.evo ? " · 已进化" : " · 未进化"),
      graduated(en));
  });
  addCheck(checks, "灵侍", B.pet ? (D.pets.find(p => p.id === B.pet) || {}).name : "未带", !!B.pet);
  addCheck(checks, "装备", Object.keys(B.equips).length + " / 10", Object.keys(B.equips).length === 10);
  addCheck(checks, "丹药", B.elixirs.length + " / 2" + (B.elixirs.length > 2 ? "（超出任务上限）" : ""), B.elixirs.length > 0 && B.elixirs.length <= 2);
  c.appendChild(checks);

  // 功法标签统计（构建后实际标签：含参悟/进化新增）
  const tagCount = new Map();
  B.skills.forEach(en => finalTags(en).forEach(t => tagCount.set(t, (tagCount.get(t) || 0) + 1)));
  if(tagCount.size){
    const p = el("div", "panel");
    p.appendChild(el("div", "sec-title", "功法标签（构建后）"));
    p.appendChild(el("p", "", "<span style='font-size:12px;color:var(--text-faint)'>✦ 虚线徽章 = 参悟/进化后才获得的标签。标签决定词条生效范围（如「弹射功法伤害+X%」）。</span>"));
    const bar = el("div", "pl-tagstat");
    [...tagCount.entries()].sort((a, b) => b[1] - a[1]).forEach(([t, n]) => {
      const base = B.skills.some(en => (SKILL_BY_ID.get(en.id).tags || []).includes(t));
      const b = el("span", "badge" + (base ? " r2" : " x"), esc(t) + " ×" + n);
      b.style.cursor = "pointer";
      b.onclick = () => { location.hash = "#/skills?tags=" + encodeURIComponent(t); };
      bar.appendChild(b);
    });
    p.appendChild(bar);
    c.appendChild(p);
  }

  // 速览
  const p = el("div", "panel");
  p.appendChild(el("div", "sec-title", "构筑速览"));
  const list = el("div", "pl-sumlist");
  if(ch){
    list.innerHTML += "<div class='h'>角色</div>" +
      esc(ch.name) + " · " + esc(ch.title || "");
    list.innerHTML += "<div class='h'>天赋</div>" +
      ch.talent_tree.map((tier, i) => B.talents[i] ? esc(talentName(ch, i, B.talents[i])) : "<span style='color:var(--text-faint)'>—</span>").join(" → ");
  }
  if(B.skills.length){
    list.innerHTML += "<div class='h'>功法</div>" + B.skills.map((en, i) => {
      const s = SKILL_BY_ID.get(en.id);
      const evo = en.evo ? "（" + esc((s.evolutions.find(e => e.id === en.evo) || {}).name || "") + "）" : "";
      return "<a href='#/skills/" + encodeURIComponent(s.id) + "'>" + esc(s.name) + "</a>" + evo;
    }).join("、");
  }
  if(Object.keys(B.equips).length){
    list.innerHTML += "<div class='h'>装备</div>" + SLOT_ORDER.filter(s => equipOf(s)).map(s => {
      const e = EQUIP_BY_ID.get(equipOf(s).id);
      return "<a href='#/equipment/" + encodeURIComponent(e.id) + "'>" + esc(e.name) + "</a>" + (s === "宝物 2" ? "²" : "");
    }).join("、");
  }
  if(B.pet){
    const pet = D.pets.find(p => p.id === B.pet);
    list.innerHTML += "<div class='h'>灵侍</div>" + esc((pet && pet.name) || "");
  }
  if(B.elixirs.length){
    list.innerHTML += "<div class='h'>丹药</div>" + B.elixirs.map(id => esc((ELIXIR_BY_ID.get(id) || {}).name || "")).join("、");
  }
  p.appendChild(list);
  c.appendChild(p);
}

function addCheck(box, label, val, ok){
  const cls = ok ? "pl-ok" : "pl-no";
  box.appendChild(el("div", "pl-check",
    "<b>" + esc(label) + "</b><span class='" + cls + "'>" + esc(val) + "</span>"));
}
function talentName(ch, tier, id){
  const t = (ch.talent_tree[tier] || []).find(x => x.id === id);
  return t ? (t.name || id) : id;
}

/* planner.js 在主脚本之后加载：若首跳就是 #/planner，主脚本 route() 时本页还未定义，
   这里补一次路由 */
if(location.hash.replace(/^#\/?/, "").split("?")[0].split("/")[0] === "planner") route();


/* ============ 导出分享图（纯 Canvas 手绘，零依赖）============ */
/* ---- 布局 B：双栏杂志 ---- */
function drawTwoColLayout(P){
  const { ch, talents, skills, equips, elixirs, tagCount, pet, cache, tw, clip } = P;
  const W = 1240, PAD = 48, GAP = 26, LW = 660;   // 左栏宽
  const RX = PAD + LW + GAP, RW = W - PAD - RX;
  const cv = document.createElement("canvas");
  const mk = (w, h) => { cv.width = w * EXPORT_SCALE; cv.height = h * EXPORT_SCALE; const c = cv.getContext("2d"); c.scale(EXPORT_SCALE, EXPORT_SCALE); return c; };
  const setF = (ctx, size, weight, color) => { ctx.font = (weight || "") + " " + size + "px " + FONT; ctx.fillStyle = color; };
  const rrect = (ctx, x, y, w, h, r) => { ctx.beginPath(); ctx.roundRect(x, y, w, h, r); };
  const pill = (ctx, x, y, text, size, color, alpha) => {
    setF(ctx, size, "", color);
    const w = ctx.measureText(text).width + 22;
    ctx.globalAlpha = alpha == null ? 1 : alpha;
    rrect(ctx, x, y, w, size + 14, (size + 14) / 2); ctx.strokeStyle = color; ctx.lineWidth = 1; ctx.stroke();
    ctx.globalAlpha = (alpha == null ? 1 : alpha) * .12; ctx.fillStyle = color; ctx.fill();
    ctx.globalAlpha = 1; ctx.fillStyle = color; ctx.textBaseline = "middle"; ctx.textAlign = "left";
    ctx.fillText(text, x + 11, y + (size + 14) / 2 + 1);
    return w;
  };
  const secT = (ctx, x, w, y, t) => {
    ctx.fillStyle = IMG.gold; ctx.fillRect(x, y + 4, 3, 16);
    setF(ctx, 16, "600", IMG.gold); ctx.textBaseline = "alphabetic"; ctx.textAlign = "left";
    ctx.fillText(t, x + 11, y + 19);
    ctx.strokeStyle = IMG.line; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(x, y + 32); ctx.lineTo(x + w, y + 32); ctx.stroke();
    return y + 44;
  };
  const bgFrame = (ctx, H) => {
    ctx.fillStyle = IMG.bg; ctx.fillRect(0, 0, W, H);
    const g = ctx.createLinearGradient(0, 0, W, 0);
    g.addColorStop(0, "#c9a86a00"); g.addColorStop(.5, "#c9a86a30"); g.addColorStop(1, "#c9a86a00");
    ctx.fillStyle = g; ctx.fillRect(0, 0, W, 4);
    ctx.strokeStyle = IMG.line; ctx.lineWidth = 1; ctx.strokeRect(14.5, 14.5, W - 29, H - 29);
  };

  // 高度预估（两栏独立）
  const tn = talents.filter(Boolean);
  const eqRowH = equips.map(x => 58 + (x.picked.length ? 24 : 0));
  let leftH = 44 + (Math.ceil(tn.length / 2) * 56 || 56) + 10;
  if(skills.length) leftH += 44 + skills.length * 92 + 6;
  let rightH = 44;
  if(equips.length) rightH += 44 + eqRowH.reduce((a, b) => a + b, 0);
  if(pet) rightH += 58;
  const pillsH = (arr, size) => { let ex = RX, rows = 1; arr.forEach(n => { const w = tw(n, size, "") + 22; if(ex + w > W - PAD){ rows++; ex = RX; } ex += w + 10; }); return rows * (size + 24) + 8; };
  if(elixirs.length) rightH += 44 + pillsH(elixirs, 13.5);
  if(tagCount.size) rightH += 44 + pillsH([...tagCount.entries()].map(([t, n]) => t + " ×" + n), 13);
  const H = 168 + Math.max(leftH, rightH) + 76;

  const ctx = mk(W, H);
  bgFrame(ctx, H);

  // 头部
  let y = 42;
  const head = cache[ch.icon ? imgSrc(ch.icon) : ""];
  if(head){ rrect(ctx, PAD, y, 80, 80, 12); ctx.save(); ctx.clip(); ctx.drawImage(head, PAD, y, 80, 80); ctx.restore();
    ctx.strokeStyle = IMG.goldDim; rrect(ctx, PAD, y, 80, 80, 12); ctx.stroke(); }
  const hx = PAD + 98;
  setF(ctx, 30, "600", IMG.gold); ctx.textBaseline = "alphabetic"; ctx.textAlign = "left";
  ctx.fillText(clip(B.name, 30, "600", 430), hx, y + 36);
  setF(ctx, 16, "", IMG.text);
  ctx.fillText(ch.name + " · " + (ch.title || ""), hx, y + 64);
  setF(ctx, 12.5, "", IMG.faint);
  ctx.fillText("功法 " + B.skills.length + "/6 · 装备 " + Object.keys(B.equips).length + "/10 · 丹药 " + elixirs.length + "/2", hx, y + 86);
  setF(ctx, 12.5, "", IMG.faint); ctx.textAlign = "right";
  ctx.fillText("太吾异世劫 Wiki · BD 构筑分享", W - PAD, y + 22);
  ctx.textAlign = "left";
  y = 132;

  // ===== 左栏：天赋 + 功法 =====
  let ly = secT(ctx, PAD, LW, y, "境界天赋");
  tn.forEach((name, i) => {
    const col = i % 2, row = Math.floor(i / 2);
    const x = PAD + col * (LW / 2 + 5), wy = ly + row * 56;
    rrect(ctx, x, wy, LW / 2 - 5, 48, 8);
    ctx.fillStyle = IMG.panel; ctx.fill(); ctx.strokeStyle = IMG.line; ctx.stroke();
    const lv = ch.talent_tree.findIndex((t, ti) => B.talents[ti] && talentName(ch, ti, B.talents[ti]) === name);
    setF(ctx, 10.5, "", IMG.goldDim); ctx.fillText(lv === 0 ? "绝学" : "境界" + lv, x + 11, wy + 15);
    setF(ctx, 14, "600", IMG.gold); ctx.fillText(clip(name, 14, "600", LW / 2 - 28), x + 11, wy + 35);
  });
  ly += Math.ceil(tn.length / 2) * 56 + 10;
  if(skills.length){
    ly = secT(ctx, PAD, LW, ly, "功法（" + skills.length + "/6）");
    skills.forEach(({ s, evo, brs, grad, pts }) => {
      rrect(ctx, PAD, ly, LW, 84, 9);
      ctx.fillStyle = IMG.panel; ctx.fill(); ctx.strokeStyle = IMG.line; ctx.stroke();
      const ic = cache[s.icon ? pl_imgSrc(s.icon) : ""];
      if(ic){ rrect(ctx, PAD + 10, ly + 10, 64, 64, 8); ctx.save(); ctx.clip(); ctx.drawImage(ic, PAD + 10, ly + 10, 64, 64); ctx.restore(); }
      const tx = PAD + 88, rSafe = PAD + LW - 92;
      setF(ctx, 19, "600", IMG.gold); ctx.fillText(clip(s.name, 19, "600", 220), tx, ly + 32);
      if(evo){
        setF(ctx, 13.5, "600", IMG.rar["绝品"]);
        const en = evo.name.replace(s.name + "·", "·");
        const nx = tx + tw(clip(s.name, 19, "600", 220), 19, "600") + 10;
        ctx.fillText(clip(en, 13.5, "600", Math.max(40, rSafe - nx)), nx, ly + 32);
      }
      ctx.textAlign = "right";
      if(grad){ setF(ctx, 13, "600", IMG.green); ctx.fillText("✓ 毕业", PAD + LW - 12, ly + 32); }
      else { setF(ctx, 12, "", IMG.faint); ctx.fillText("参悟 " + pts + "/9", PAD + LW - 12, ly + 32); }
      ctx.textAlign = "left";
      setF(ctx, 12.5, "", IMG.dim);
      ctx.fillText(clip(brs.length ? brs.join(" · ") + "（满级）" : "（未选分支）", 12.5, "", LW - 110), tx, ly + 58);
      const tags = [...finalTags(B.skills.find(x => x.id === s.id) || {})];
      setF(ctx, 11, "", IMG.faint);
      ctx.fillText(clip(tags.slice(0, 8).join(" / "), 11, "", LW - 110), tx, ly + 76);
      ly += 92;
    });
  }

  // ===== 右栏：装备 + 灵侍 + 丹药 + 标签 =====
  let ry = secT(ctx, RX, RW, y, "装备（" + equips.length + "/10）");
  equips.forEach(({ slot, e, picked }, i) => {
    const rowH = eqRowH[i];
    rrect(ctx, RX, ry, RW, rowH - 6, 8);
    ctx.fillStyle = IMG.panel; ctx.fill(); ctx.strokeStyle = IMG.line; ctx.stroke();
    const ic = cache[e.icon ? imgSrc(e.icon) : ""];
    if(ic){ rrect(ctx, RX + 9, ry + 7, 32, 32, 6); ctx.save(); ctx.clip(); ctx.drawImage(ic, RX + 9, ry + 7, 32, 32); ctx.restore(); }
    const ex0 = RX + 52;
    setF(ctx, 10.5, "", IMG.faint); ctx.fillText(slot, ex0, ry + 16);
    const nmW = tw(clip(e.name, 14.5, "600", 240), 14.5, "600");
    setF(ctx, 14.5, "600", IMG.text); ctx.fillText(clip(e.name, 14.5, "600", 240), ex0, ry + 36);
    setF(ctx, 12, "600", IMG.rar[e.rarity] || IMG.dim);
    ctx.fillText(e.rarity, ex0 + nmW + 12, ry + 36);
    if(picked.length){
      setF(ctx, 11.5, "", IMG.gold);
      ctx.fillText(clip(picked.map(t => "◎ " + t).join("　"), 11.5, "", RW - 24), ex0, ry + rowH - 16);
    }
    ry += rowH;
  });
  if(pet){
    rrect(ctx, RX, ry, RW, 46, 8);
    ctx.fillStyle = IMG.panel; ctx.fill(); ctx.strokeStyle = IMG.line; ctx.stroke();
    const pt = D.pets.find(pp => pp.id === B.pet) || {};
    const pic = pt.icon ? cache[imgSrc(pt.icon)] : null;
    if(pic){ rrect(ctx, RX + 9, ry + 6, 34, 34, 6); ctx.save(); ctx.clip(); ctx.drawImage(pic, RX + 9, ry + 6, 34, 34); ctx.restore(); }
    setF(ctx, 10.5, "", IMG.faint); ctx.fillText("灵侍", RX + 52, ry + 16);
    setF(ctx, 14.5, "600", IMG.text); ctx.fillText(clip(pt.name || "", 14.5, "600", 140), RX + 52, ry + 36);
    setF(ctx, 12, "600", pt.kind === "宛渠之民" ? IMG.rar["绝品"] : IMG.rar["奇品"]);
    ctx.fillText(pt.kind || "", RX + 52 + tw(clip(pt.name || "", 14.5, "600", 140), 14.5, "600") + 12, ry + 36);
    ry += 58;
  }
  if(elixirs.length){
    ry = secT(ctx, RX, RW, ry, "丹药（" + elixirs.length + "/2）");
    let ex = RX;
    elixirs.forEach(n => { const w = tw(n, 13.5, "") + 22; if(ex + w > W - PAD){ ex = RX; ry += 37; } pill(ctx, ex, ry, n, 13.5, IMG.text, .9); ex += w + 10; });
    ry += 44;
  }
  if(tagCount.size){
    ry = secT(ctx, RX, RW, ry, "功法标签");
    let ex = RX;
    [...tagCount.entries()].sort((a, b) => b[1] - a[1]).forEach(([t, n]) => {
      const label = t + " ×" + n;
      const w = tw(label, 13, "") + 22;
      if(ex + w > W - PAD){ ex = RX; ry += 35; }
      pill(ctx, ex, ry, label, 13, IMG.gold, .8);
      ex += w + 10;
    });
  }

  // 底部
  setF(ctx, 11.5, "", IMG.faint); ctx.textAlign = "center";
  ctx.fillText("数据来自游戏内真实数值 · 太吾异世劫 Wiki · 生成于 " + new Date().toLocaleDateString("zh-CN"), W / 2, H - 30);
  return cv;
}

/* ---- 布局 C：极简海报（无图标 · 纯文字 · 大留白）---- */
function drawMinimalLayout(P){
  const { ch, talents, skills, equips, elixirs, tagCount, pet, tw, clip } = P;
  const W = 920, PAD = 84;
  const cv = document.createElement("canvas");
  const mk = (h) => { cv.width = W * EXPORT_SCALE; cv.height = h * EXPORT_SCALE; const c = cv.getContext("2d"); c.scale(EXPORT_SCALE, EXPORT_SCALE); return c; };
  const setF = (ctx, size, weight, color) => { ctx.font = (weight || "") + " " + size + "px " + FONT; ctx.fillStyle = color; };

  // 高度
  const tn = talents.filter(Boolean);
  const petObj = B.pet ? (D.pets.find(pp => pp.id === B.pet) || {}) : null;
  let H = 210;
  H += 60 + Math.ceil(tn.length / 3) * 54;
  if(skills.length) H += 60 + skills.length * 64;
  if(equips.length) H += 60 + equips.length * 44;
  if(petObj) H += 44;
  if(elixirs.length){ let ex = PAD, rows = 1; elixirs.forEach(n => { const w = tw(n, 14, "") + 26; if(ex + w > W - PAD){ rows++; ex = PAD; } ex += w + 10; }); H += 60 + rows * 40; }
  if(tagCount.size){ let ex = PAD, rows = 1; [...tagCount.entries()].forEach(([t, n]) => { const w = tw(t + " ×" + n, 13, "") + 26; if(ex + w > W - PAD){ rows++; ex = PAD; } ex += w + 10; }); H += 60 + rows * 36; }
  H += 90;

  const ctx = mk(H);
  ctx.fillStyle = IMG.bg; ctx.fillRect(0, 0, W, H);
  const g = ctx.createLinearGradient(0, 0, W, 0);
  g.addColorStop(0, "#c9a86a00"); g.addColorStop(.5, "#c9a86a38"); g.addColorStop(1, "#c9a86a00");
  ctx.fillStyle = g; ctx.fillRect(0, 0, W, 3);

  const hr = (y, w) => { ctx.strokeStyle = IMG.line; ctx.lineWidth = 1; ctx.beginPath(); ctx.moveTo(W / 2 - w / 2, y); ctx.lineTo(W / 2 + w / 2, y); ctx.stroke(); };
  const secT = (y, t) => {
    const tw2 = ctx.measureText(t).width;
    setF(ctx, 14, "600", IMG.gold); ctx.textAlign = "center"; ctx.textBaseline = "alphabetic";
    ctx.fillText("　" + t + "　", W / 2, y + 18);
    ctx.strokeStyle = IMG.line;
    ctx.beginPath(); ctx.moveTo(PAD, y + 13); ctx.lineTo(W / 2 - tw2 / 2 - 26, y + 13); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(W / 2 + tw2 / 2 + 26, y + 13); ctx.lineTo(W - PAD, y + 13); ctx.stroke();
    ctx.textAlign = "left";
    return y + 44;
  };

  // 头部（居中）
  setF(ctx, 34, "600", IMG.gold); ctx.textAlign = "center";
  ctx.fillText(clip(B.name, 34, "600", W - PAD * 2), W / 2, 86);
  setF(ctx, 16, "", IMG.text);
  ctx.fillText(ch.name + " · " + (ch.title || ""), W / 2, 122);
  setF(ctx, 12.5, "", IMG.faint);
  ctx.fillText("功法 " + B.skills.length + "/6 · 装备 " + Object.keys(B.equips).length + "/10 · 丹药 " + elixirs.length + "/2", W / 2, 148);
  hr(172, 320);
  let y = 196;

  // 天赋（3 列纯文字）
  y = secT(y, "境界天赋");
  tn.forEach((name, i) => {
    const col = i % 3, row = Math.floor(i / 3);
    const x = PAD + col * ((W - PAD * 2) / 3);
    const lv = ch.talent_tree.findIndex((t, ti) => B.talents[ti] && talentName(ch, ti, B.talents[ti]) === name);
    setF(ctx, 11, "", IMG.goldDim); ctx.textAlign = "left";
    ctx.fillText(lv === 0 ? "绝学" : "境界" + lv, x, y + row * 54 + 15);
    setF(ctx, 16, "600", IMG.text);
    ctx.fillText(clip(name, 16, "600", (W - PAD * 2) / 3 - 16), x, y + row * 54 + 40);
  });
  y += Math.ceil(tn.length / 3) * 54 + 16;

  // 功法（两行式 + 分隔线）
  if(skills.length){
    y = secT(y, "功法");
    skills.forEach(({ s, evo, brs, grad, pts }, i) => {
      setF(ctx, 18, "600", IMG.gold); ctx.textAlign = "left";
      ctx.fillText(clip(s.name, 18, "600", 260), PAD, y + 26);
      if(evo){
        setF(ctx, 13.5, "600", IMG.rar["绝品"]);
        const en = evo.name.replace(s.name + "·", "·");
        ctx.fillText(en, PAD + tw(clip(s.name, 18, "600", 260), 18, "600") + 12, y + 26);
      }
      ctx.textAlign = "right";
      setF(ctx, 12.5, "", grad ? IMG.green : IMG.faint);
      ctx.fillText(grad ? "✓ 毕业" : "参悟 " + pts + "/9" + (evo ? "" : " · 未进化"), W - PAD, y + 26);
      ctx.textAlign = "left";
      setF(ctx, 12.5, "", IMG.dim);
      ctx.fillText(clip(brs.length ? brs.join(" · ") + "（满级）" : "（未选分支）", 12.5, "", W - PAD * 2), PAD, y + 48);
      if(i < skills.length - 1){ ctx.strokeStyle = "#2a231a"; ctx.beginPath(); ctx.moveTo(PAD, y + 60); ctx.lineTo(W - PAD, y + 60); ctx.stroke(); }
      y += 64;
    });
    y += 16;
  }

  // 装备（一行一件）
  if(equips.length){
    y = secT(y, "装备");
    equips.forEach(({ slot, e, picked }) => {
      setF(ctx, 11.5, "", IMG.goldDim); ctx.textAlign = "left";
      ctx.fillText(slot, PAD, y + 26);
      setF(ctx, 15, "600", IMG.text);
      ctx.fillText(clip(e.name, 15, "600", 300), PAD + 58, y + 27);
      setF(ctx, 12.5, "600", IMG.rar[e.rarity] || IMG.dim);
      ctx.fillText(e.rarity, PAD + 58 + tw(clip(e.name, 15, "600", 300), 15, "600") + 12, y + 27);
      ctx.textAlign = "right";
      setF(ctx, 12, "", picked.length ? IMG.gold : IMG.faint);
      ctx.fillText(picked.length ? "◎ 目标词条 ×" + picked.length : "—", W - PAD, y + 27);
      ctx.textAlign = "left";
      y += 44;
    });
    y += 16;
  }

  if(petObj){
    y = secT(y, "灵侍");
    setF(ctx, 15, "600", IMG.text); ctx.textAlign = "left";
    ctx.fillText(petObj.name || "", PAD, y + 22);
    setF(ctx, 12.5, "600", petObj.kind === "宛渠之民" ? IMG.rar["绝品"] : IMG.rar["奇品"]);
    ctx.fillText(petObj.kind || "", PAD + tw(petObj.name || "", 15, "600") + 12, y + 22);
    y += 44;
  }

  if(elixirs.length){
    y = secT(y, "丹药（" + elixirs.length + "/2）");
    let ex = PAD;
    elixirs.forEach(n => {
      const w = tw(n, 14, "") + 26;
      if(ex + w > W - PAD){ ex = PAD; y += 40; }
      setF(ctx, 14, "", IMG.text);
      ctx.strokeStyle = IMG.line2; ctx.lineWidth = 1;
      ctx.beginPath(); ctx.roundRect(ex, y, w, 32, 16); ctx.stroke();
      ctx.textBaseline = "middle"; ctx.textAlign = "left";
      ctx.fillText(n, ex + 13, y + 17);
      ex += w + 10;
    });
    ctx.textBaseline = "alphabetic";
    y += 60;
  }
  if(tagCount.size){
    y = secT(y, "功法标签");
    let ex = PAD;
    [...tagCount.entries()].sort((a, b) => b[1] - a[1]).forEach(([t, n]) => {
      const label = t + " ×" + n;
      const w = tw(label, 13, "") + 26;
      if(ex + w > W - PAD){ ex = PAD; y += 36; }
      setF(ctx, 13, "", IMG.gold);
      ctx.strokeStyle = IMG.goldDim;
      ctx.beginPath(); ctx.roundRect(ex, y, w, 29, 15); ctx.stroke();
      ctx.textBaseline = "middle"; ctx.textAlign = "left";
      ctx.fillText(label, ex + 13, y + 15);
      ex += w + 10;
    });
    ctx.textBaseline = "alphabetic";
  }

  hr(H - 58, 320);
  setF(ctx, 11.5, "", IMG.faint); ctx.textAlign = "center";
  ctx.fillText("数据来自游戏内真实数值 · 太吾异世劫 Wiki · 生成于 " + new Date().toLocaleDateString("zh-CN"), W / 2, H - 30);
  return cv;
}


/* ---- 布局 D：分享卡（2 列卡片墙 · 角色灵侍 / 功法 / 装备分区，先大画布后裁边）---- */
function drawShareLayout(P){
  const { ch, talents, skills, equips, elixirs, cache } = P;
  const W = 920, PAD = 30, GAP = 14, CW = Math.floor((W - PAD * 2 - GAP) / 2);
  const petObj = B.pet ? (D.pets.find(p => p.id === B.pet) || null) : null;
  const F2 = '"Noto Sans SC","Microsoft YaHei","PingFang SC",sans-serif';
  const C = { bg: "#191916", card: "#21211d", line: "#32322a", gold: "#d2bc84",
              bright: "#e9e5d6", dim: "#a6a294", faint: "#71705f", lv: "#c8c2ad",
              green: "#7ec97e", lead: "#3d3c32", hd: "#8f8a76" };
  const cv = document.createElement("canvas");
  const mctx = cv.getContext("2d");
  const setF = (ctx, size, weight, color) => { ctx.font = (weight ? weight + " " : "") + size + "px " + F2; ctx.fillStyle = color; };
  const mw = (s, size, weight) => { setF(mctx, size, weight, "#fff"); return mctx.measureText(s).width; };
  const clipF = (s, size, weight, maxW) => {
    if(mw(s, size, weight) <= maxW) return s;
    let t = s;
    while(t.length > 1 && mw(t + "…", size, weight) > maxW) t = t.slice(0, -1);
    return t + "…";
  };
  const wrap = (text, size, maxW) => {
    setF(mctx, size, "", "#fff");
    const out = [];
    String(text).split(/\n/).forEach(par => {
      let cur = "";
      for(const chr of par){
        if(cur && mctx.measureText(cur + chr).width > maxW){ out.push(cur); cur = chr; }
        else cur += chr;
      }
      out.push(cur);
    });
    return out.length ? out : [""];
  };
  /* 公开版构建会整行删除含公式模板字面量的行，这里用拼接正则规避（公开数据已静态求值，删与不删等价） */
  const cleanTxt = s => String(s || "").replace(new RegExp("\\{\"f[^{}]*\\}", "g"), "X").replace(/<[^>]*>/g, "").replace(/\s+/g, " ").trim();
  const rrect = (ctx, x, y, w, h, r) => { ctx.beginPath(); ctx.roundRect(x, y, w, h, r); };
  const shell = (ctx, x, y, w, h) => {
    rrect(ctx, x, y, w, h, 10); ctx.fillStyle = C.card; ctx.fill();
    ctx.strokeStyle = C.line; ctx.lineWidth = 1; ctx.stroke();
  };
  /* 卡头：圆角图标 + 名称 + 副题段 + 右上角状态，返回正文起始 y */
  const head = (ctx, x, y, iconKey, name, subSegs, extra) => {
    const ic = cache[iconKey];
    if(ic){ rrect(ctx, x + 14, y + 14, 42, 42, 8); ctx.save(); ctx.clip(); ctx.drawImage(ic, x + 14, y + 14, 42, 42); ctx.restore(); }
    setF(ctx, 16, "600", C.gold); ctx.textBaseline = "alphabetic"; ctx.textAlign = "left";
    ctx.fillText(clipF(name, 16, "600", CW - 150), x + 68, y + 32);
    if(extra){ setF(ctx, 11, "", extra.color || C.faint); ctx.textAlign = "right"; ctx.fillText(extra.text, x + CW - 14, y + 31); ctx.textAlign = "left"; }
    let cx = x + 68;
    (subSegs || []).forEach(seg => { setF(ctx, 11.5, seg[2] || "", seg[1]); ctx.fillText(seg[0], cx, y + 50); cx += mw(seg[0], 11.5, seg[2] || ""); });
    return y + 68;
  };
  /* 普通行：小图标（缺图用◆）+ 名称 + 右侧灰标签 */
  const iconRow = (ctx, x, y, iconKey, name, label) => {
    const ic = cache[iconKey];
    if(ic){ rrect(ctx, x + 14, y + 4, 20, 20, 4); ctx.save(); ctx.clip(); ctx.drawImage(ic, x + 14, y + 4, 20, 20); ctx.restore(); }
    else { setF(ctx, 9, "", C.gold); ctx.fillText("◆", x + 19, y + 18); }
    setF(ctx, 13, "", C.bright);
    ctx.fillText(clipF(name, 13, "", CW - 150), x + 42, y + 18);
    if(label){ setF(ctx, 11.5, "", C.faint); ctx.textAlign = "right"; ctx.fillText(label, x + CW - 14, y + 18); ctx.textAlign = "left"; }
  };
  /* 等级行：名称 + 虚线引导 + Lv 值 */
  const lvRow = (ctx, x, y, iconKey, name, lvText) => {
    const ic = cache[iconKey];
    if(ic){ rrect(ctx, x + 14, y + 3, 20, 20, 4); ctx.save(); ctx.clip(); ctx.drawImage(ic, x + 14, y + 3, 20, 20); ctx.restore(); }
    else { setF(ctx, 9, "", C.gold); ctx.fillText("◆", x + 19, y + 17); }
    setF(ctx, 13, "", C.bright);
    const nm = clipF(name, 13, "", CW - 160);
    ctx.fillText(nm, x + 42, y + 17);
    setF(ctx, 12, "600", C.lv);
    ctx.textAlign = "right"; ctx.fillText(lvText, x + CW - 14, y + 17); ctx.textAlign = "left";
    const x1 = x + 42 + mw(nm, 13, "") + 10, x2 = x + CW - 14 - mw(lvText, 12, "600") - 10;
    if(x2 > x1 + 12){
      ctx.strokeStyle = C.lead; ctx.lineWidth = 1; ctx.setLineDash([2, 4]);
      ctx.beginPath(); ctx.moveTo(x1, y + 13); ctx.lineTo(x2, y + 13); ctx.stroke();
      ctx.setLineDash([]);
    }
  };
  const wrapBlock = (ctx, x, y, text, size, color, maxW, lh) => {
    const lines = wrap(text, size, maxW);
    setF(ctx, size, "", color);
    lines.forEach((ln, i) => ctx.fillText(ln, x, y + i * lh));
    return y + lines.length * lh;
  };
  const secT = (ctx, y, t, n) => {
    setF(ctx, 16, "600", C.gold); ctx.textBaseline = "alphabetic"; ctx.textAlign = "left";
    ctx.fillText(t, PAD, y + 15);
    if(n != null){ setF(ctx, 13, "", C.faint); ctx.fillText(String(n), PAD + mw(t, 16, "600") + 10, y + 15); }
    return y + 36;
  };

  /* ---- 角色卡 ---- */
  const pickedTiers = [];
  ch.talent_tree.forEach((tier, ti) => {
    const t = tier.find(x => B.talents[ti] === x.id);
    if(t) pickedTiers.push({ ti, t });
  });
  const topTier = pickedTiers.length ? pickedTiers[pickedTiers.length - 1].ti : -1;
  const charH = 68 + Math.max(pickedTiers.length, 1) * 30 + 30 + 10;
  const drawChar = (ctx, x, y, hH) => {
    shell(ctx, x, y, CW, hH);
    let cy = head(ctx, x, y, ch.icon ? imgSrc(ch.icon) : "", ch.name,
      [["角色 · " + (topTier < 0 ? "未选天赋" : topTier === 0 ? "初始绝学" : "境界 " + topTier), C.faint]]);
    if(pickedTiers.length) pickedTiers.forEach(({ ti, t }) => {
      iconRow(ctx, x, cy, t.icon ? pl_imgSrc(t.icon) : "", t.name, ti === 0 ? "绝学" : "境界 " + ti);
      cy += 30;
    });
    else { setF(ctx, 12.5, "", C.faint); ctx.fillText("（未选天赋）", x + 14, cy + 18); }
    ctx.font = "italic 11px " + F2; ctx.fillStyle = C.faint;
    ctx.fillText("每境界选择 1 项用于规划，互斥与叠加以游戏内效果为准。", x + 14, y + hH - 13);
  };
  /* ---- 灵侍卡 ---- */
  const petRows = petObj
    ? (petObj.skills || []).map(s => ({ name: s.name, label: "技能" }))
        .concat((petObj.evolve_skills || []).map(n => ({ name: n, label: "进化" })))
    : [];
  const petPassive = petObj ? (petPassiveText(petObj) || cleanTxt(petObj.desc)) : "";
  const petH = petObj ? 68 + Math.max(petRows.length, 1) * 28 +
    (petPassive ? wrap(petPassive, 12.5, CW - 46).length * 18 + 8 : 0) + 10 : 0;
  const drawPet = (ctx, x, y, hH) => {
    shell(ctx, x, y, CW, hH);
    let cy = head(ctx, x, y, petObj.icon ? imgSrc(petObj.icon) : "", petObj.name || "灵侍",
      [[(petObj.kind || "灵侍") + (petObj.species && petObj.species !== "特殊" ? " · " + petObj.species : ""), C.faint]]);
    if(petRows.length) petRows.forEach(r => { iconRow(ctx, x, cy, "", r.name, r.label); cy += 28; });
    else { setF(ctx, 12.5, "", C.faint); ctx.fillText("（无技能数据）", x + 14, cy + 18); }
    if(petPassive) wrapBlock(ctx, x + 14, cy + 14, petPassive, 12.5, C.dim, CW - 46, 18);
  };
  /* ---- 功法卡 ---- */
  const skCards = skills.map(({ s, evo, grad, pts }) => {
    const en = B.skills.find(x => x.id === s.id) || { lv: [] };
    const rows = s.upgrades.map((br, bi) => ({ name: branchName(br), lv: en.lv[bi], icon: br.icon })).filter(r => r.lv > 0);
    return { s, evo, rows, grad, pts, h: 68 + Math.max(rows.length, 1) * 28 + 10 };
  });
  const drawSkill = (c, ctx, x, y, hH) => {
    shell(ctx, x, y, CW, hH);
    let cy = head(ctx, x, y, c.s.icon ? pl_imgSrc(c.s.icon) : "", c.s.name,
      [[c.evo ? c.evo.name : (c.s.tags || []).slice(0, 4).join(" / "), C.faint]],
      { text: c.grad ? "✓ 毕业" : "参悟 " + c.pts + "/9" + (c.evo ? "" : " · 未进化"), color: c.grad ? C.green : C.faint });
    if(c.rows.length) c.rows.forEach(r => { lvRow(ctx, x, cy, r.icon ? pl_imgSrc(r.icon) : "", r.name, "Lv" + r.lv); cy += 28; });
    else { setF(ctx, 12.5, "", C.faint); ctx.fillText("（未选分支）", x + 14, cy + 18); }
  };
  /* ---- 装备卡：词条只写名称（去尾部数值区间，过长折 2 行）——固有 prop 金字 / 固有特效 ✦ 全文 / 目标 ◎ ---- */
  const traitName = s => String(s || "").replace(PL_RANGE_SUF, "").trim();
  const eqCards = equips.map(({ slot, e, picked }) => {
    const rows = [];
    (e.fixed_traits || []).forEach(t => {
      if(t.prop && (t.range_t2 || t.range)) rows.push({ k: "prop", text: traitName(t.name) || t.prop });
      else rows.push({ k: "fx", text: fxText(t) });
    });
    (picked || []).forEach(t => rows.push({ k: "pick", text: traitName(t) }));
    let h = 68 + 10;
    rows.forEach(r => {
      h += r.k === "fx" ? wrap(r.text, 12.5, CW - 46).length * 18 + 6
                        : Math.min(wrap(r.text, 13, r.k === "prop" ? CW - 28 : CW - 46).length, 2) * 18 + 4;
    });
    return { slot, e, rows, h };
  });
  const drawEquip = (c, ctx, x, y, hH) => {
    shell(ctx, x, y, CW, hH);
    head(ctx, x, y, c.e.icon ? imgSrc(c.e.icon) : "", c.e.name,
      [[SLOT_CAT(c.slot) + " · ", C.faint], [c.e.rarity, IMG.rar[c.e.rarity] || C.faint, "600"]]);
    let cy = y + 68;
    c.rows.forEach(r => {
      if(r.k === "prop"){
        const lines = wrap(r.text, 13, CW - 28).slice(0, 2);
        setF(ctx, 13, "600", C.gold);
        lines.forEach((ln, i) => ctx.fillText(ln, x + 14, cy + 12 + i * 18));
        cy += lines.length * 18 + 4;
      }else if(r.k === "pick"){
        setF(ctx, 12.5, "600", C.gold); ctx.fillText("◎", x + 14, cy + 12);
        const lines = wrap(r.text, 13, CW - 46).slice(0, 2);
        setF(ctx, 13, "", C.bright);
        lines.forEach((ln, i) => ctx.fillText(ln, x + 32, cy + 12 + i * 18));
        cy += lines.length * 18 + 4;
      }else{
        setF(ctx, 12.5, "600", C.gold); ctx.fillText("✦", x + 14, cy + 12);
        cy = wrapBlock(ctx, x + 32, cy + 12, r.text, 12.5, C.dim, CW - 46, 18) + 6;
      }
    });
  };

  /* ---- 先在大画布上顺序画，再按实际高度裁边 ---- */
  const estH = 130 + 36 + Math.max(charH, petH)
    + (skCards.length ? 36 + Math.ceil(skCards.length / 2) * (Math.max(...skCards.map(c => c.h)) + GAP) : 0)
    + (eqCards.length ? 36 + Math.ceil(eqCards.length / 2) * (Math.max(...eqCards.map(c => c.h)) + GAP) : 0)
    + (elixirs.length ? 60 + elixirs.length * 38 : 0) + 320;
  cv.width = W * EXPORT_SCALE; cv.height = estH * EXPORT_SCALE;
  const ctx = cv.getContext("2d");
  ctx.scale(EXPORT_SCALE, EXPORT_SCALE);
  ctx.fillStyle = C.bg; ctx.fillRect(0, 0, W, estH);
  ctx.textBaseline = "alphabetic"; ctx.textAlign = "left";

  let y = 34;
  setF(ctx, 12.5, "", C.hd); ctx.fillText("太吾异世劫 · 构筑分享", PAD, y);
  y += 14;
  setF(ctx, 26, "600", C.gold);
  ctx.fillText(clipF(B.name || "未命名构筑", 26, "600", W - PAD * 2), PAD, y + 24);
  y += 38;
  setF(ctx, 12.5, "", C.dim);
  ctx.fillText("功法 " + B.skills.length + "/6　装备 " + Object.keys(B.equips).length + "/10" +
    (elixirs.length ? "　丹药 " + elixirs.length + "/2" : ""), PAD, y + 14);
  y += 30;

  y = secT(ctx, y, "角色特质与随行灵侍", 1 + (petObj ? 1 : 0));
  {
    const hRow = Math.max(charH, petH);
    drawChar(ctx, PAD, y, hRow);
    if(petObj) drawPet(ctx, PAD + CW + GAP, y, hRow);
    y += hRow + 18;
  }
  if(skCards.length){
    y = secT(ctx, y, "功法配置", skCards.length);
    for(let r = 0; r * 2 < skCards.length; r++){
      const pair = [skCards[r * 2], skCards[r * 2 + 1]].filter(Boolean);
      const hRow = Math.max(...pair.map(c => c.h));
      pair.forEach((c, k) => drawSkill(c, ctx, PAD + k * (CW + GAP), y, hRow));
      y += hRow + GAP;
    }
    y += 14;
  }
  if(eqCards.length){
    y = secT(ctx, y, "装备配置", eqCards.length);
    for(let r = 0; r * 2 < eqCards.length; r++){
      const pair = [eqCards[r * 2], eqCards[r * 2 + 1]].filter(Boolean);
      const hRow = Math.max(...pair.map(c => c.h));
      pair.forEach((c, k) => drawEquip(c, ctx, PAD + k * (CW + GAP), y, hRow));
      y += hRow + GAP;
    }
    y += 14;
  }
  if(elixirs.length){
    y = secT(ctx, y, "丹药", elixirs.length);
    let ex = PAD;
    elixirs.forEach(n => {
      const w2 = mw(n, 12.5, "") + 26;
      if(ex + w2 > W - PAD){ ex = PAD; y += 34; }
      rrect(ctx, ex, y, w2, 28, 14); ctx.strokeStyle = C.line; ctx.lineWidth = 1; ctx.stroke();
      setF(ctx, 12.5, "", C.bright); ctx.textBaseline = "middle"; ctx.fillText(n, ex + 13, y + 15); ctx.textBaseline = "alphabetic";
      ex += w2 + 8;
    });
    y += 34 + 14;
  }

  y += 10;
  setF(ctx, 11, "", C.faint);
  ctx.fillText("太吾异世劫 Wiki · BD 构筑台 · 数据 " + ((D.meta || {}).updated || ""), PAD, y + 12);
  ctx.textAlign = "right";
  ctx.fillText("生成于 " + new Date().toLocaleDateString("zh-CN"), W - PAD, y + 12);
  ctx.textAlign = "left";
  const H = y + 40;

  const out = document.createElement("canvas");
  out.width = W * EXPORT_SCALE; out.height = H * EXPORT_SCALE;
  const octx = out.getContext("2d");
  octx.fillStyle = C.bg; octx.fillRect(0, 0, out.width, out.height);
  octx.drawImage(cv, 0, 0);
  return out;
}


const IMG = {
  bg: "#14100c", panel: "#221c15", panel2: "#2a231a", line: "#3a3125",
  gold: "#c9a86a", goldDim: "#8f7a52", red: "#de5a4e", green: "#7ec97e",
  text: "#e8e0d0", dim: "#a89c86", faint: "#6e6452",
  rar: { "凡品": "#9aa7b5", "奇品": "#5fa8d6", "极品": "#b07fe0", "绝品": "#e0a94a" }
};
const FONT = '"Noto Serif SC","Source Han Serif SC","Songti SC",SimSun,serif';
const EXPORT_SCALE = 2;   // 分享图 2x 超采样导出（高分屏/放大查看不发糊）

function pl_imgSrc(name){ return "icons/" + encodeURIComponent(name) + ".png"; }

async function exportBuildImage(){
  if(!B.char){ alert("先选择角色，再导出图片"); return; }
  const ch = charOf();
  const W = 1000, PAD = 44;
  const ctx0 = document.createElement("canvas").getContext("2d");

  // ---- 预加载图标（失败则跳过）----
  const cache = {};
  async function load(src){
    if(!src || cache[src] !== undefined) return cache[src] || null;
    cache[src] = null;
    try{
      cache[src] = await new Promise((res, rej) => {
        const i = new Image(); i.onload = () => res(i); i.onerror = rej; i.src = src;
      });
    }catch(e){}
    return cache[src];
  }
  const need = [];
  if(ch.icon) need.push(imgSrc(ch.icon));
  B.skills.forEach(en => { const s = SKILL_BY_ID.get(en.id); if(s && s.icon) need.push(pl_imgSrc(s.icon)); });
  B.skills.forEach(en => { const s = SKILL_BY_ID.get(en.id); if(s) s.upgrades.forEach((br, bi) => { if(en.lv[bi] && br.icon) need.push(pl_imgSrc(br.icon)); }); });
  ch.talent_tree.forEach((tier, ti) => { const t = tier.find(x => B.talents[ti] === x.id); if(t && t.icon) need.push(pl_imgSrc(t.icon)); });
  const ptExp = B.pet ? D.pets.find(p => p.id === B.pet) : null;
  if(ptExp && ptExp.icon) need.push(imgSrc(ptExp.icon));
  SLOT_ORDER.forEach(sl => { const o = equipOf(sl); if(o){ const e = EQUIP_BY_ID.get(o.id); if(e && e.icon) need.push(imgSrc(e.icon)); } });
  await Promise.all(need.map(load));

  // ---- 文本工具 ----
  const F = (size, weight, color) => { ctx0.font = (weight || "") + " " + size + "px " + FONT; ctx0.fillStyle = color; return ctx0; };
  const tw = (s, size, weight) => { F(size, weight, IMG.text); return ctx0.measureText(s).width; };
  const clip = (s, size, weight, maxW) => {
    if(tw(s, size, weight) <= maxW) return s;
    let t = s;
    while(t.length > 1 && tw(t + "…", size, weight) > maxW) t = t.slice(0, -1);
    return t + "…";
  };

  // ---- 数据快照 ----
  const talents = ch.talent_tree.map((tier, i) => B.talents[i] ? talentName(ch, i, B.talents[i]) : null);
  const skills = B.skills.map((en, i) => {
    const s = SKILL_BY_ID.get(en.id);
    const evo = s.evolutions.find(e => e.id === en.evo) || null;
    const brs = s.upgrades.map((b, bi) => en.lv[bi] ? branchName(b) : null).filter(Boolean);
    return { i, s, evo, brs, grad: graduated(en), pts: points(en) };
  });
  const equips = SLOT_ORDER.map(sl => {
    const o = equipOf(sl); if(!o) return null;
    const e = EQUIP_BY_ID.get(o.id);
    const picked = [];
    (e.random_trait_pools || []).forEach((pool, pi) => { ((o.picks || {})[pi] || []).forEach(t => picked.push(t)); });
    return { slot: sl, e, picked };
  }).filter(Boolean);
  const elixirs = B.elixirs.map(id => (ELIXIR_BY_ID.get(id) || {}).name).filter(Boolean);
  const tagCount = new Map();
  B.skills.forEach(en => finalTags(en).forEach(t => tagCount.set(t, (tagCount.get(t) || 0) + 1)));

  // ---- 高度计算 ----
  const secH = 52;                 // 区块标题行
  let H = 168;                     // 头部
  H += secH + (Math.ceil(talents.filter(Boolean).length / 3) || 1) * 58 + 10;   // 天赋 3 列
  if(skills.length) H += secH + Math.ceil(skills.length / 2) * 106 + 8;
  const pet = B.pet ? (D.pets.find(p => p.id === B.pet) || {}).name : null;
  if(pet) H += 52;
  if(equips.length) H += secH + equips.reduce((a, x) => a + 54 + (x.picked.length ? 28 : 0), 0) + 8;
  if(elixirs.length){
    let ex = PAD + 76, rows = 1;
    const f2 = (size, w) => { ctx0.font = (w || "") + " " + size + "px " + FONT; };
    elixirs.forEach(n => { f2(15); const w = ctx0.measureText(n).width + 30; if(ex + w > W - PAD){ rows++; ex = PAD + 76; } ex += w + 10; });
    H += secH + rows * 38 + 8;
  }
  if(tagCount.size){
    const tags = [...tagCount.entries()];
    let ex = PAD + 76, rows = 1;
    tags.forEach(([t, n]) => { const label = t + " ×" + n; const w = tw(label, 14) + 26; if(ex + w > W - PAD){ rows++; ex = PAD + 76; } ex += w + 10; });
    H += secH + rows * 38 + 8;
  }
  H += 88;                          // 底部

  const cv = document.createElement("canvas");
  cv.width = W * EXPORT_SCALE; cv.height = H * EXPORT_SCALE;
  const ctx = cv.getContext("2d");
  ctx.scale(EXPORT_SCALE, EXPORT_SCALE);
  const setF = (size, weight, color) => { ctx.font = (weight || "") + " " + size + "px " + FONT; ctx.fillStyle = color; };
  const rrect = (x, y, w, h, r) => { ctx.beginPath(); ctx.roundRect(x, y, w, h, r); };
  const pill = (x, y, text, size, color, alpha) => {
    setF(size, "", color);
    const w = ctx.measureText(text).width + 22;
    ctx.globalAlpha = alpha == null ? 1 : alpha;
    rrect(x, y, w, size + 14, (size + 14) / 2); ctx.strokeStyle = color; ctx.lineWidth = 1; ctx.stroke();
    ctx.globalAlpha = (alpha == null ? 1 : alpha) * 0.12; ctx.fillStyle = color; ctx.fill();
    ctx.globalAlpha = 1; ctx.fillStyle = color;
    ctx.textBaseline = "middle"; ctx.textAlign = "left";
    ctx.fillText(text, x + 11, y + (size + 14) / 2 + 1);
    return w;
  };
  const secTitle = (y, t) => {
    ctx.fillStyle = IMG.gold; ctx.fillRect(PAD, y + 6, 4, 20);
    setF(20, "600", IMG.gold); ctx.textBaseline = "alphabetic"; ctx.textAlign = "left";
    ctx.fillText(t, PAD + 14, y + 24);
    ctx.strokeStyle = IMG.line; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(PAD, y + 40); ctx.lineTo(W - PAD, y + 40); ctx.stroke();
    return y + 52;
  };

  // ---- 排版分叉：分享卡 / 双栏 / 极简 走独立绘制 ----
  if(LAYOUT === "share"){
    const pack = { ch, talents, skills, equips, elixirs, tagCount, pet, cache, tw, clip };
    await finishExport(drawShareLayout(pack));
    return;
  }
  if(LAYOUT === "twocol" || LAYOUT === "minimal"){
    const pack = { ch, talents, skills, equips, elixirs, tagCount, pet, cache, tw, clip };
    await finishExport(LAYOUT === "twocol" ? drawTwoColLayout(pack) : drawMinimalLayout(pack));
    return;
  }

  // ---- 背景 ----
  ctx.fillStyle = IMG.bg; ctx.fillRect(0, 0, W, H);
  const grad = ctx.createLinearGradient(0, 0, W, 0);
  grad.addColorStop(0, "#c9a86a00"); grad.addColorStop(.5, "#c9a86a30"); grad.addColorStop(1, "#c9a86a00");
  ctx.fillStyle = grad; ctx.fillRect(0, 0, W, 4);
  ctx.strokeStyle = IMG.line; ctx.lineWidth = 1; ctx.strokeRect(14.5, 14.5, W - 29, H - 29);

  // ---- 头部 ----
  let y = 44;
  const head = cache[ch.icon ? imgSrc(ch.icon) : ""];
  if(head){ rrect(PAD, y, 88, 88, 12); ctx.save(); ctx.clip(); ctx.drawImage(head, PAD, y, 88, 88); ctx.restore();
    ctx.strokeStyle = IMG.goldDim; rrect(PAD, y, 88, 88, 12); ctx.stroke(); }
  let hx = PAD + (head ? 106 : 0);
  setF(34, "600", IMG.gold); ctx.textBaseline = "alphabetic"; ctx.textAlign = "left";
  ctx.fillText(clip(B.name, 34, "600", 460), hx, y + 38);
  setF(17, "", IMG.text);
  ctx.fillText(ch.name + " · " + (ch.title || ""), hx, y + 66);
  setF(13, "", IMG.faint);
  ctx.fillText("功法 " + B.skills.length + "/6 · 装备 " + Object.keys(B.equips).length + "/10 · 丹药 " + elixirs.length, hx, y + 90);
  setF(13, "", IMG.faint); ctx.textAlign = "right";
  ctx.fillText("太吾异世劫 Wiki · BD 构筑分享", W - PAD, y + 24);
  ctx.textAlign = "left";
  y += 124;

  // ---- 天赋 ----
  y = secTitle(y, "境界天赋");
  const tn = talents.filter(Boolean);
  const tGap = 12, tCw = (W - PAD * 2 - tGap * 2) / 3;   // 等宽等距 3 列
  tn.forEach((name, i) => {
    const col = i % 3, row = Math.floor(i / 3);
    const x = PAD + col * (tCw + tGap), wy = y + row * 58;
    rrect(x, wy, tCw, 48, 8);
    ctx.fillStyle = IMG.panel; ctx.fill(); ctx.strokeStyle = IMG.line; ctx.stroke();
    const lv = ch.talent_tree.findIndex((t, ti) => B.talents[ti] && talentName(ch, ti, B.talents[ti]) === name);
    setF(11, "", IMG.goldDim); ctx.fillText(lv === 0 ? "绝学" : "境界" + lv, x + 12, wy + 15);
    setF(15, "600", IMG.gold); ctx.fillText(clip(name, 15, "600", tCw - 24), x + 12, wy + 36);
  });
  y += Math.ceil(tn.length / 3) * 58 + 10;

  // ---- 功法（2 列窄卡）----
  if(skills.length){
    y = secTitle(y, "功法（" + skills.length + "/6）");
    const sGap = 12, sCw = Math.floor((W - PAD * 2 - sGap) / 2);
    skills.forEach(({ s, evo, brs, grad, pts }, i) => {
      const col = i % 2, row = Math.floor(i / 2);
      const x = PAD + col * (sCw + sGap), wy = y + row * 106;
      rrect(x, wy, sCw, 96, 10);
      ctx.fillStyle = IMG.panel; ctx.fill(); ctx.strokeStyle = IMG.line; ctx.stroke();
      const ic = cache[s.icon ? pl_imgSrc(s.icon) : ""];
      if(ic){ rrect(x + 12, wy + 12, 56, 56, 8); ctx.save(); ctx.clip(); ctx.drawImage(ic, x + 12, wy + 12, 56, 56); ctx.restore(); }
      const tx = x + 80;
      const bodyW = sCw - 92;                       // 正文可用宽
      // 名字行 + 右上角状态
      setF(18, "600", IMG.gold); ctx.textBaseline = "alphabetic";
      const nmW = tw(clip(s.name, 18, "600", 150), 18, "600");
      ctx.fillText(clip(s.name, 18, "600", 150), tx, wy + 32);
      if(evo){
        setF(12.5, "600", IMG.rar["绝品"]);
        const en = evo.name.replace(s.name + "·", "·");
        const evoW = Math.max(36, x + sCw - 12 - 96 - (tx + nmW + 8));   // 右侧留状态文字 ~96px
        ctx.fillText(clip(en, 12.5, "600", evoW), tx + nmW + 8, wy + 32);
      }
      ctx.textAlign = "right";
      if(grad){ setF(12, "600", IMG.green); ctx.fillText("✓ 毕业", x + sCw - 12, wy + 32); }
      else { setF(11, "", IMG.faint); ctx.fillText("参悟 " + pts + "/9" + (evo ? "" : " · 未进化"), x + sCw - 12, wy + 32); }
      ctx.textAlign = "left";
      setF(12.5, "", IMG.dim);
      ctx.fillText(clip(brs.length ? brs.join(" · ") + "（满级）" : "（未选分支）", 12.5, "", bodyW), tx, wy + 58);
      const tags = [...finalTags(B.skills.find(x2 => x2.id === s.id) || {})];
      setF(11, "", IMG.faint);
      ctx.fillText(clip(tags.slice(0, 6).join(" / "), 11, "", bodyW), tx, wy + 80);
      // 图标下方小序号
      setF(11, "", IMG.faint); ctx.textAlign = "center";
      ctx.fillText("#" + (i + 1), x + 40, wy + 84);
      ctx.textAlign = "left";
    });
    y += Math.ceil(skills.length / 2) * 106 + 8;
  }

  // ---- 宠物 ----
  if(pet){
    rrect(PAD, y, W - PAD * 2, 42, 8);
    ctx.fillStyle = IMG.panel; ctx.fill(); ctx.strokeStyle = IMG.line; ctx.stroke();
    setF(13, "", IMG.goldDim); ctx.fillText("灵侍", PAD + 14, y + 26);
    setF(16, "600", IMG.text); ctx.fillText(pet, PAD + 66, y + 27);
    y += 52;
  }

  // ---- 装备 ----
  if(equips.length){
    y = secTitle(y, "装备（" + equips.length + "/10 · ◎ 为目标词条）");
    // 2 列窄卡；词条逐条流式换行，卡高按词条行数自适应，同行两卡取高
    const eGap = 12, eCw = Math.floor((W - PAD * 2 - eGap) / 2);
    const traitLines = picked => {
      const lines = []; let cur = "";
      picked.forEach(t => {
        const piece = "◎ " + t;
        const tryLine = cur ? cur + "　" + piece : piece;
        if(tw(tryLine, 12, "") <= eCw - 96) cur = tryLine;
        else { if(cur) lines.push(cur); cur = tw(piece, 12, "") <= eCw - 96 ? piece : ""; }
      });
      if(cur) lines.push(cur);
      return lines.length ? lines : (picked.length ? [""] : []);
    };
    const cardH = eq => 44 + traitLines(eq.picked).length * 19;
    for(let r = 0; r * 2 < equips.length; r++){
      const pair = [equips[r * 2], equips[r * 2 + 1]].filter(Boolean);
      const hRow = Math.max(...pair.map(cardH));
      pair.forEach(({ slot, e, picked }, k) => {
        const x = PAD + k * (eCw + eGap), wy = y;
        const h = cardH({ picked });
        rrect(x, wy, eCw, h, 8);
        ctx.fillStyle = IMG.panel; ctx.fill(); ctx.strokeStyle = IMG.line; ctx.stroke();
        const ic = cache[e.icon ? imgSrc(e.icon) : ""];
        if(ic){ rrect(x + 10, wy + 8, 30, 30, 6); ctx.save(); ctx.clip(); ctx.drawImage(ic, x + 10, wy + 8, 30, 30); ctx.restore(); }
        const ex0 = x + 52, bodyW = eCw - 64;
        setF(11, "", IMG.faint); ctx.fillText(slot + " · " + e.rarity, ex0, wy + 18);
        setF(15, "600", IMG.text);
        ctx.fillText(clip(e.name, 15, "600", bodyW), ex0, wy + 38);
        setF(12, "", IMG.gold);
        traitLines(picked).forEach((ln, li) => {
          if(ln) ctx.fillText(clip(ln, 12, "", bodyW), ex0, wy + 56 + li * 19);
        });
      });
      y += hRow + 8;
    }
  }

  // ---- 丹药 ----
  if(elixirs.length){
    y = secTitle(y, "丹药（" + elixirs.length + "）");
    let ex = PAD, ey = y;
    elixirs.forEach(n => {
      const w = tw(n, 14, "") + 22;
      if(ex + w > W - PAD){ ex = PAD; ey += 38; }
      pill(ex, ey, n, 14, IMG.text, .9);
      ex += w + 10;
    });
    y = ey + 38 + 8;
  }

  // ---- 标签统计 ----
  if(tagCount.size){
    y = secTitle(y, "功法标签（构建后）");
    let ex = PAD, ey = y;
    [...tagCount.entries()].sort((a, b) => b[1] - a[1]).forEach(([t, n]) => {
      const label = t + " ×" + n;
      const w = tw(label, 14, "") + 22;
      if(ex + w > W - PAD){ ex = PAD; ey += 38; }
      pill(ex, ey, label, 14, IMG.gold, .8);
      ex += w + 10;
    });
    y = ey + 38 + 8;
  }

  // ---- 底部 ----
  setF(12, "", IMG.faint); ctx.textAlign = "center";
  ctx.fillText("数据来自游戏内真实数值 · 太吾异世劫 Wiki · 生成于 " + new Date().toLocaleDateString("zh-CN"), W / 2, H - 34);
  ctx.textAlign = "left";

  await finishExport(cv);
}

async function finishExport(cv){
  const btn = [...document.querySelectorAll(".pl-toolbar .pl-btn")].find(b => b.textContent.indexOf("复制图片") >= 0 || b.textContent.indexOf("已复制") >= 0);
  const flash = (t) => { if(!btn) return; btn.textContent = t; setTimeout(() => { btn.textContent = "复制图片"; }, 1600); };
  try{
    const blob = await new Promise(res => cv.toBlob(res, "image/png"));
    window.__plExportBlob = blob;   // 调试/自动化钩子
    try{
      await navigator.clipboard.write([new ClipboardItem({ "image/png": blob })]);
      flash("✓ 已复制到剪贴板");
    }catch(e){
      const a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      a.download = (B.name || "BD构筑") + ".png";
      a.click();
      setTimeout(() => URL.revokeObjectURL(a.href), 4000);
      flash("剪贴板不可用 · 已下载");
    }
  }catch(err){
    alert("导出失败：请通过桌面快捷方式 / http 方式打开 Wiki（直接双击 html 文件无法导出图片）");
  }
}

})();
