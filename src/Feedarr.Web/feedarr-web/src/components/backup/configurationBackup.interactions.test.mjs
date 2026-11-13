import test from "node:test";
import { configurationFileError } from "./configurationBackupApi.js";
import assert from "node:assert/strict";
import { build } from "esbuild";
import { fileURLToPath } from "node:url";

// Same lightweight hook scheduler as the existing maintenance action tests.
async function load(file) {
  const bundle = await build({ entryPoints: [fileURLToPath(new URL(file, import.meta.url))], bundle: true, platform: "node", format: "esm", write: false,
    plugins: [{ name: "controlled-ui", setup(b) {
      b.onResolve({ filter: /^react$/ }, () => ({ path: "hooks", namespace: "test" }));
      b.onResolve({ filter: /useSettingsController\.js$/ }, () => ({ path: "settings-controller", namespace: "test" }));
      b.onResolve({ filter: /useSubbar\.js$/ }, () => ({ path: "subbar", namespace: "test" }));
      b.onResolve({ filter: /(?:SubAction|Loader|SettingsGeneral|SettingsProviders|SettingsBackup|SettingsMaintenance|SettingsUiPage|SettingsSecurityPage|SettingsArrPage)\.jsx$/ }, args => args.importer ? ({ path: "settings-child", namespace: "child" }) : undefined);
      b.onResolve({ filter: /auth\/session\.js$/ }, () => ({ path: "auth", namespace: "test" }));
      b.onResolve({ filter: /^react-router-dom$/ }, () => ({ path: "router", namespace: "test" }));
      b.onResolve({ filter: /api\/client\.js$/ }, () => ({ path: "api", namespace: "test" }));
      b.onResolve({ filter: /configurationBackupApi\.js$/ }, () => ({ path: "backup", namespace: "test" }));
      b.onResolve({ filter: /(?:InlineNotice|AppIcon|Modal|Step\w+|SetupAccess|ConfigurationBackupPanel)\.jsx$/ }, args => args.importer ? ({ path: args.path, namespace: "child" }) : undefined);
      b.onLoad({ filter: /.*/, namespace: "child" }, () => ({ contents: 'export default function Child() {} export function ConfigurationPreview() {}' }));
      b.onLoad({ filter: /.*/, namespace: "test" }, args => ({ contents: {
        hooks: `const element=(type,props,...children)=>type?.name==="ConfigurationArchiveInput"?type(props):({type,props:{...props,children:children.flat(Infinity)}}); export default {createElement:element,lazy:()=>function Lazy(){}}; export const Suspense="Suspense";
          export const useCallback=fn=>fn; export const useMemo=fn=>fn();
          export const useSyncExternalStore=(_subscribe,snapshot)=>snapshot();
          export function useState(initial){const h=globalThis.__configUI,i=h.cursor++; if(!(i in h.values))h.values[i]=typeof initial==='function'?initial():initial; return [h.values[i],v=>{h.values[i]=typeof v==='function'?v(h.values[i]):v;}];}
          export function useRef(initial){const h=globalThis.__configUI,i=h.cursor++; if(!(i in h.values))h.values[i]={current:initial};return h.values[i];}
          export function useEffect(fn,deps){const h=globalThis.__configUI,i=h.cursor++,old=h.deps[i];if(!old||deps.some((d,j)=>d!==old[j])){h.deps[i]=deps;h.effects.push(fn);}}`,
        auth: 'export const subscribeSession=()=>()=>{}; export const getSessionSnapshot=()=>({state:globalThis.__configUI.authState}); export const refreshSession=async()=>({setupCompleted:false}); export const login=(...a)=>globalThis.__configUI.login(...a);',
        router: 'export const useNavigate=()=>globalThis.__configUI.navigate; export const useLocation=()=>({pathname:globalThis.__configUI.path||"/settings/backup"});',
        subbar: 'export const useSubbarSetter=()=>globalThis.__configUI.setContent;',
        'settings-controller': 'export default section=>({...globalThis.__configUI.controller,showBackup:section==="backup",showGeneral:section==="general"});',
        api: 'export const apiGet=(...a)=>globalThis.__configUI.get(...a); export const apiPost=(...a)=>globalThis.__configUI.post(...a); export const apiPut=(...a)=>globalThis.__configUI.put(...a);',
        backup: `export const configurationFileError=${configurationFileError.toString()}; export const MAX_CONFIGURATION_UPLOAD=64*1024*1024; export const backupRoot=initial=>initial?'/api/setup/configuration-restore':'/api/configuration-backups';
          export const configurationRequest=(path,...a)=>path==="/api/setup/state"?globalThis.__configUI.get(path,...a):globalThis.__configUI.request(path,...a);
          export const configurationUpload=(file,phrase)=>{if(!file?.size||file.size>MAX_CONFIGURATION_UPLOAD)throw new Error("Sélectionnez une archive");return {file,phrase};}; export const downloadConfiguration=(blob)=>globalThis.__configUI.download?.(blob);
          export const resumeSetupSteps=steps=>Array.isArray(steps)?steps:null;`,
      }[args.path] }));
    } }], });
  return (await import(`data:text/javascript;base64,${Buffer.from(bundle.outputFiles[0].text).toString("base64")}`)).default;
}
const Panel = await load("./ConfigurationBackupPanel.jsx");
const Settings = await load("../../pages/Settings.jsx");
const BackupSettings = await load("../../pages/settings/SettingsBackup.jsx");
const flush = async () => { for(let i=0;i<12;i++) await Promise.resolve(); };
const nodes = tree => !tree || typeof tree !== "object" ? [] : [tree,...(tree.props?.children||[]).flatMap(nodes)];
const text = tree => typeof tree === "string" ? tree : (tree?.props?.message || "") + (tree?.props?.children||[]).map(text).join("");
const button = (tree,label) => nodes(tree).find(n=>n.type==="button" && text(n).startsWith(label));
function fixture() {
  const previous={window:globalThis.window,ui:globalThis.__configUI}; const data=new Map();
  const h={cursor:0,values:[],deps:[],effects:[],navigate:()=>{},get:async()=>({}),post:async()=>{},request:async()=>({})};
  globalThis.__configUI=h; globalThis.window={localStorage:{getItem:k=>data.get(k)??null,setItem:(k,v)=>data.set(k,v),removeItem:k=>data.delete(k)},setInterval:()=>1,clearInterval:()=>{},confirm:()=>true};
  return {h,data,render:(Component,props={})=>{h.cursor=0;return Component(props);},effects:async()=>{for(const fn of h.effects.splice(0))fn();await flush();},close:()=>{globalThis.window=previous.window;globalThis.__configUI=previous.ui;}};
}
test("import clears phrase, prevents duplicate submissions and requires both confirmations", async()=>{
  const f=fixture();let finish, calls=0;f.h.request=(path)=>{if(path.endsWith('/prepare')){calls++;return new Promise(r=>{finish=r;});}return Promise.resolve({});};
  try {
    let tree=f.render(Panel); const inputs=nodes(tree).filter(n=>n.type==='input');
    inputs.find(n=>n.props.type==='file').props.onChange({target:{files:[new File(['zip'],'backup.zip')]}});
    inputs.at(-1).props.onChange({target:{value:'private phrase'}});tree=f.render(Panel);
    const submit=button(tree,'Analyser la sauvegarde');const pending=submit.props.onClick();submit.props.onClick();assert.equal(calls,1);
    tree=f.render(Panel);assert.equal(nodes(tree).filter(n=>n.type==='input').at(-1).props.value,'');assert.equal(button(tree,'Analyse en cours…').props.disabled,true);
    finish({operationId:'a'.repeat(32),securityChangeRequiresConfirmation:true});await pending;tree=f.render(Panel);
    assert.equal(button(tree,'Confirmer la restauration').props.disabled,true);
    let checks=nodes(tree).filter(n=>n.type==='input'&&n.props.type==='checkbox');checks[0].props.onChange({target:{checked:true}});tree=f.render(Panel);assert.equal(button(tree,'Confirmer la restauration').props.disabled,true);
    checks=nodes(tree).filter(n=>n.type==='input'&&n.props.type==='checkbox');checks[1].props.onChange({target:{checked:true}});assert.equal(button(f.render(Panel),'Confirmer la restauration').props.disabled,false);
    assert.equal(f.data.size,0);
  } finally {f.close();}
});
test("retention and initial sync remain separately confirmed and sync blocked by pending retention",()=>{
  const f=fixture();try{f.render(Panel);f.h.values[0]={applied:{operationId:'a',retentionPending:true,syncState:'held',completedSources:0,totalSources:1}};
    const tree=f.render(Panel);assert.equal(button(tree,'Autoriser la première synchronisation').props.disabled,true);
    let calls=0;f.h.request=()=>{calls++;};globalThis.window.confirm=()=>false;button(tree,'Activer la rétention importée').props.onClick();assert.equal(calls,0);
  }finally{f.close();}
});
test("export validates visibly, prevents duplicates and exposes download success or failure",async()=>{
  const f=fixture();let finish,calls=0,downloaded=0;
  const submit=tree=>nodes(tree).find(n=>n.type==='form').props.onSubmit({preventDefault(){}});
  const fill=phrase=>{for(const n of nodes(f.render(Panel)).filter(n=>n.type==='input'&&n.props.type==='password').slice(0,2))n.props.onChange({target:{value:phrase}});};
  try {
    submit(f.render(Panel));await flush();assert.ok(nodes(f.render(Panel)).some(n=>/12 à/.test(n.props?.message)));
    fill('long secret phrase');let tree=f.render(Panel);nodes(tree).filter(n=>n.type==='input'&&n.props.type==='password')[1].props.onChange({target:{value:'different'}});
    submit(f.render(Panel));await flush();assert.ok(nodes(f.render(Panel)).some(n=>/identiques/.test(n.props?.message)));
    fill('long secret phrase');f.h.download=()=>downloaded++;
    f.h.request=(path,options)=>{calls++;assert.ok(path.endsWith('/export'));assert.equal(options.binary,true);assert.deepEqual(options.body,{passphrase:'long secret phrase'});return new Promise(r=>{finish=r;});};
    tree=f.render(Panel);submit(tree);submit(tree);assert.equal(calls,1);assert.ok(nodes(f.render(Panel)).some(n=>n.props?.message==='Chiffrement et préparation du téléchargement…'));
    assert.equal(nodes(f.render(Panel)).find(n=>n.type==='input').props.value,'');finish(new File(['zip'],'backup.zip'));await flush();assert.equal(downloaded,1);
    assert.ok(nodes(f.render(Panel)).some(n=>n.props?.variant==='success'&&/Téléchargement/.test(n.props.message)));
    fill('long secret phrase');f.h.request=async()=>{throw new Error('Autorisation requise');};submit(f.render(Panel));await flush();assert.ok(nodes(f.render(Panel)).some(n=>n.props?.message==='Autorisation requise'));
  }finally{f.close();}
});
test("retention and synchronization actions use the native modal and only send after confirmation",async()=>{
  for(const [label,action] of [['Activer la rétention importée','retention'],['Autoriser la première synchronisation','sync'],['Annuler cette synchronisation','sync/cancel']]){
    const f=fixture();let calls=0;try{
      f.render(Panel);f.h.values[0]={applied:{operationId:'operation',retentionPending:action==='retention',syncState:'held'}};
      f.h.request=async(path,options)=>{calls++;assert.equal(path,`/api/configuration-backups/operation/${action}`);assert.deepEqual(options.body,{confirm:true});return {applied:{operationId:'operation'}};};
      button(f.render(Panel),label).props.onClick();let tree=f.render(Panel);assert.equal(calls,0);assert.ok(nodes(tree).some(n=>n.props?.open===true&&n.props.title==='Confirmer l’action'));
      button(tree,'Annuler').props.onClick();assert.equal(calls,0);button(f.render(Panel),label).props.onClick();await button(f.render(Panel),'Confirmer').props.onClick();assert.equal(calls,1);
    }finally{f.close();}
  }
});

test("backup header registration follows phrase validity, uses latest values and reports export states",async()=>{
  const f=fixture();let actions,finish,calls=0,downloaded=0;
  const props={onActionsChange:next=>{actions=next;}};
  const render=()=>f.render(Panel,props);
  const publish=async()=>{const tree=render();await f.effects();return tree;};
  const fill=(phrase,confirmation=phrase)=>{const inputs=nodes(render()).filter(n=>n.type==='input'&&n.props.type==='password');inputs[0].props.onChange({target:{value:phrase}});inputs[1].props.onChange({target:{value:confirmation}});};
  try{
    let tree=await publish();assert.equal(actions.canExport,false);assert.match(actions.validationMessage,/12 à/);assert.equal(button(tree,'Sauvegarder la configuration'),undefined);
    fill('too short');await publish();assert.equal(actions.canExport,false);
    fill('long secret phrase','different phrase');tree=await publish();assert.equal(actions.canExport,false);assert.match(actions.validationMessage,/identiques/);assert.ok(nodes(tree).some(n=>n.props?.['aria-invalid']===true));
    fill('long secret phrase');await publish();assert.equal(actions.canExport,true);const originalAction=actions.export;
    fill('another valid secret');await publish();f.h.download=()=>downloaded++;
    f.h.request=(path,options)=>{calls++;assert.ok(path.endsWith('/export'));assert.equal(options.body.passphrase,'another valid secret');return new Promise(r=>{finish=r;});};
    const pending=originalAction();originalAction();await publish();assert.equal(calls,1);assert.equal(actions.canExport,false);assert.equal(actions.busy,true);assert.equal(actions.saveState,'loading');
    finish(new File(['zip'],'backup.zip'));await pending;await publish();assert.equal(actions.saveState,'success');assert.equal(actions.canExport,false);assert.equal(downloaded,1);
    fill('long secret phrase');await publish();f.h.request=async()=>{throw new Error('Export refusé');};await actions.export();tree=await publish();assert.equal(actions.saveState,'error');assert.ok(nodes(tree).some(n=>n.props?.message==='Export refusé'));
  }finally{f.close();}
});
test("parent subbar owns backup save/refresh actions, follows native visual states and removes save on navigation",async()=>{
  const f=fixture();let header,saved=0,refreshed=0,lists=0,states=0;
  f.h.setContent=content=>{header=content;};
  f.h.controller={settingsTitle:'Sauvegarde',loading:false,maintenance:{},backup:{loadBackups:()=>lists++,loadBackupState:()=>states++}};
  const Page=f.render(Settings).type;
  const render=section=>f.render(Page,{section});
  const action=label=>nodes(header).find(n=>n.props?.label===label);
  try{
    let tree=render('backup');await f.effects();assert.equal(action('Sauvegarder').props.disabled,true);assert.equal(action('Rafraîchir').props.disabled,true);
    const register=nodes(tree).find(n=>n.props?.onActionsChange).props.onActionsChange;
    for(const [state,icon,cls] of [['idle','save',''],['loading','progress_activity','is-loading'],['success','check_circle','is-success'],['error','cancel','is-error']]){
      register({canExport:state==='idle',busy:state==='loading',saveState:state,validationMessage:'',export:()=>saved++,refresh:()=>refreshed++});render('backup');await f.effects();
      assert.equal(action('Sauvegarder').props.icon,icon);assert.equal(action('Sauvegarder').props.className,cls);assert.equal(action('Sauvegarder').props.disabled,state!=='idle');
      if(state==='idle'){action('Sauvegarder').props.onClick();action('Rafraîchir').props.onClick();assert.equal(saved,1);assert.equal(refreshed,1);assert.equal(lists,1);assert.equal(states,1);}
      if(state==='loading')assert.equal(action('Rafraîchir').props.disabled,true);
    }
    register({canExport:false,busy:false,saveState:'idle',validationMessage:'Les phrases doivent être identiques.'});render('backup');await f.effects();assert.equal(action('Sauvegarder').props.disabled,true);assert.match(action('Sauvegarder').props.title,/identiques/);
    register(null);render('general');await f.effects();assert.equal(action('Sauvegarder'),undefined);assert.ok(action('Enregistrer'));
  }finally{f.close();}
});
test("legacy archive rows expose delete and a named confirmation with busy/error feedback", () => {
  const f = fixture(); let target, confirmed = 0;
  const props = { backups: [{ name: "local archive.zip", sizeBytes: 2048 }], openBackupDelete: item => { target = item; },
    backupDeleteOpen: true, backupDeleteTarget: { name: "local archive.zip" }, handleBackupDelete: () => { confirmed++; }, closeBackupDelete: () => {} };
  try {
    let tree = f.render(BackupSettings, props);
    button(tree, "Supprimer").props.onClick(); assert.equal(target.name, "local archive.zip"); assert.equal(confirmed, 0);
    let modal = nodes(tree).find(n => n.props?.title === "Supprimer l’archive locale");
    assert.equal(modal.props.open, true); assert.match(text(modal), /local archive.zip/);
    button(modal, "Supprimer").props.onClick(); assert.equal(confirmed, 1);
    tree = f.render(BackupSettings, { ...props, backupDeleteLoading: true, backupError: "Accès refusé" });
    modal = nodes(tree).find(n => n.props?.title === "Supprimer l’archive locale");
    assert.equal(modal.props.closeDisabled, true); assert.equal(button(modal, "Suppression…").props.disabled, true);
    assert.ok(nodes(modal).some(n => n.props?.variant === "error" && n.props.message === "Accès refusé"));
    tree = f.render(BackupSettings, { backups: [], backupNotice: "Sauvegarde supprimée." });
    assert.ok(nodes(tree).some(n => n.props?.variant === "success" && n.props.message === "Sauvegarde supprimée."));
  } finally { f.close(); }
});

test("settings and wizard use the same archive input and reject invalid selection before analysis", () => {
  const f = fixture();
  try {
    let tree = f.render(Panel), select = nodes(tree).find(n => n.type === "input" && n.props.type === "file");
    assert.equal(button(tree, "Analyser la sauvegarde").props.disabled, true);
    select.props.onChange({ target: { files: [new File(["x"], "wrong.txt")] } }); tree = f.render(Panel);
    assert.equal(button(tree, "Analyser la sauvegarde").props.disabled, true);
    assert.ok(nodes(tree).some(n => n.props?.variant === "error" && n.props.message?.includes("Format incorrect")));
    select = nodes(tree).find(n => n.type === "input" && n.props.type === "file");
    select.props.onChange({ target: { files: [new File(["x"], "good.zip")] } }); tree = f.render(Panel);
    assert.equal(button(tree, "Analyser la sauvegarde").props.disabled, false);
    assert.ok(nodes(tree).some(n => n.props?.variant === "success" && n.props.message?.includes("good.zip") && n.props.message.includes("contenu non encore vérifié")));
    button(tree, "Annuler la sélection").props.onClick(); tree = f.render(Panel);
    assert.equal(button(tree, "Analyser la sauvegarde").props.disabled, true);
    assert.ok(nodes(tree).some(n => n.props?.variant === "warning" && n.props.message === "Aucune archive sélectionnée — 64 Mio maximum"));
  } finally { f.close(); }
});
