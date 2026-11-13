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
const Language = await load("../setup/Step0Language.jsx");
const Security = await load("../setup/Step2Security.jsx");
const Panel = await load("./ConfigurationBackupPanel.jsx");
const Restore = await load("../setup/useSetupRestore.js");
const Settings = await load("../../pages/Settings.jsx");
const RestoreView = await load("../setup/StepConfigurationRestore.jsx");
const Wizard = await load("../../pages/SetupWizard.jsx");
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
test("wizard offers both paths and resume omits all already complete steps despite stale browser state",async()=>{
  const f=fixture();try{
    let tree=f.render(Wizard);assert.ok(button(tree,'Nouvelle installation'));assert.equal(nodes(tree).some(n=>n.props?.className==='setup-stepper'),false);button(tree,'Restaurer une configuration').props.onClick();tree=f.render(Wizard);assert.equal(nodes(tree).filter(n=>n.props?.restore).length,1);
    assert.deepEqual(nodes(tree).filter(n=>n.props?.className?.startsWith('setup-stepper-item')).map(n=>text(n.props.children[1])), ['Importer','Vérifier','Restaurer','Finaliser']);
    assert.ok(nodes(tree).some(n=>n.props?.className==='setupWizardFooter'));
    button(tree,'Retour au choix').props.onClick();await flush();tree=f.render(Wizard);assert.equal(nodes(tree).some(n=>n.props?.className==='setup-stepper'),false);
    button(tree,'Nouvelle installation').props.onClick();tree=f.render(Wizard);
    assert.equal(nodes(tree).filter(n=>n.props?.className?.startsWith('setup-stepper-item')).length,8);
  }finally{f.close();}
  const resumed=fixture();try{
    resumed.data.set('feedarr:jackettConfigured','true');
    resumed.data.set('feedarr:onboardingDone','true');
    let finishOnboarding; const onboarding=new Promise(resolve=>{finishOnboarding=resolve;});
    resumed.h.get=async path=>path.endsWith('/state')?{restoreSteps:[3,8],authRequired:true,authConfigured:false}:onboarding;
    resumed.render(Wizard);await resumed.effects();const tree=resumed.render(Wizard);
    assert.equal(button(tree,'Retour au choix').props.disabled,true);
    assert.ok(nodes(tree).some(n=>/déjà appliquée/.test(n.props?.message)));
    assert.deepEqual(nodes(tree).filter(n=>n.props?.className?.startsWith('setup-stepper-item')).map(n=>n.props.children[0].props.children[0]),[3,8]);
    assert.equal(nodes(tree).find(n=>n.props?.['aria-current']==='step').props.children[0].props.children[0],3);
    finishOnboarding({onboardingDone:false}); await flush();
    assert.equal(nodes(resumed.render(Wizard)).find(n=>n.props?.['aria-current']==='step').props.children[0].props.children[0],3);
    button(tree,'Resume').props.onClick();assert.ok(nodes(resumed.render(Wizard)).some(n=>n.type==='input'&&n.props.type==='password'));
  }finally{resumed.close();}
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
test("restore wizard analyzes once, requires both consents, handles confirm errors, waits for restart and finalizes incomplete setup",async()=>{
  const f=fixture();let finish,calls=0,applied;
  const render=()=>{f.h.cursor=0;return Restore(true,(setup,bootstrap)=>{applied={setup,bootstrap};});};
  try {
    await render().prepare();assert.match(render().error,/Sélectionnez/);
    let r=render();r.setFile(new File(['zip'],'backup.zip'));r.setPhrase('secret');
    f.h.request=(path,options)=>{calls++;assert.equal(options.body.phrase,'secret');return new Promise(resolve=>{finish=resolve;});};
    r=render();const pending=r.prepare();r.prepare();assert.equal(calls,1);assert.equal(render().busy,true);assert.equal(render().phrase,'');
    finish({operationId:'x',securityChangeRequiresConfirmation:true});await pending;r=render();assert.equal(r.stage,2);assert.equal(r.locked,true);assert.equal(r.canContinue,false);
    r.go(3);assert.equal(render().stage,2);r.setConsent(true);r=render();assert.equal(r.canContinue,false);r.setSecurityConsent(true);r=render();r.go(3);assert.equal(render().stage,3);
    f.h.request=async()=>{throw new Error('Confirmation refusée');};await render().confirm();assert.equal(render().error,'Confirmation refusée');assert.equal(render().stage,3);
    f.h.request=async(path,options)=>{assert.deepEqual(options.body,{confirm:true,securityChange:true});return {pending:{operationId:'x'},confirmed:true};};await render().confirm();r=render();r.go(2);assert.equal(render().stage,3);assert.equal(r.canVisit(1),false);
    f.h.request=async()=>({pending:{operationId:'x'},confirmed:true});await render().checkRestart();assert.match(render().notice,/pas encore/);
    f.h.request=async()=>({applied:{operationId:'x'}});await render().checkRestart();assert.equal(render().stage,4);
    f.h.get=async()=>({restoreSteps:[3,8],onboardingDone:false});await render().finish();assert.deepEqual(applied.setup.restoreSteps,[3,8]);
  }finally{f.close();}
});
test("restore back cancels the server preview and preserves file; changing path clears secrets",async()=>{
  const f=fixture();const render=()=>f.render(Restore,true);
  try {
    let r=render();r.setFile(new File(['zip'],'backup.zip'));r.setPhrase('secret');r.setBootstrap('bootstrap');f.h.request=async()=>({operationId:'x'});await render().prepare();
    f.h.request=async(path,options)=>{assert.ok(path.endsWith('/x'));assert.equal(options.method,'DELETE');};await render().cancel();r=render();assert.equal(r.stage,1);assert.equal(r.locked,false);assert.ok(r.file);assert.equal(r.consent,false);
    r.reset();r=render();assert.equal(r.file,null);assert.equal(r.phrase,'');assert.equal(r.bootstrap,'');
  }finally{f.close();}
});
test("restore reload resumes confirmed/applied stages and exposes state errors",async()=>{
  for(const [state,stage] of [[{pending:{operationId:'x'},confirmed:true},3],[{applied:{operationId:'x'}},4]]){
    const f=fixture();try{f.h.request=async()=>state;f.render(Restore,true);await f.effects();assert.equal(f.render(Restore,true).stage,stage);}finally{f.close();}
  }
  const f=fixture();try{f.h.request=async()=>{throw new Error('Bootstrap requis');};f.render(Restore,true);await f.effects();assert.equal(f.render(Restore,true).error,'Bootstrap requis');}finally{f.close();}
});
test("classic wizard retains language validation, next/back navigation and security save behavior",()=>{
  const f=fixture();try{
    let tree=f.render(Wizard);button(tree,'Nouvelle installation').props.onClick();tree=f.render(Wizard);assert.equal(button(tree,'Suivant').props.disabled,true);
    nodes(tree).find(n=>n.props?.onStatusChange).props.onStatusChange({ready:true,saving:false});tree=f.render(Wizard);assert.equal(button(tree,'Suivant').props.disabled,false);
    button(tree,'Suivant').props.onClick();tree=f.render(Wizard);assert.ok(button(tree,'Precedent'));button(tree,'Precedent').props.onClick();tree=f.render(Wizard);assert.equal(button(tree,'Suivant').props.disabled,false);
    button(tree,'Suivant').props.onClick();button(f.render(Wizard),'Suivant').props.onClick();tree=f.render(Wizard);
    const security=nodes(tree).find(n=>n.props?.saveRef);let saved=0;security.props.saveRef.current=()=>saved++;button(tree,'Enregistrer').props.onClick();assert.equal(saved,1);
    security.props.onStatusChange({saved:true,ready:true});assert.ok(button(f.render(Wizard),'Suivant'));
  }finally{f.close();}
});
test("a fully restored setup reaches Finaliser even though configuration endpoints are now closed",async()=>{
  const f=fixture();try{
    f.h.get=async()=>({onboardingDone:true});f.h.request=async()=>{throw new Error('setup_closed');};
    await f.render(Restore,true).checkRestart();const r=f.render(Restore,true);assert.equal(r.stage,4);assert.equal(r.error,'');
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
test("welcome has no stepper and classic return does not write configuration",()=>{
  const f=fixture();let writes=0;f.h.post=()=>writes++;f.h.request=()=>{writes++;return Promise.resolve({});};
  try{
    let tree=f.render(Wizard);assert.match(text(tree),/Bienvenue dans Feedarr/);assert.equal(nodes(tree).some(n=>n.props?.className==='setup-stepper'),false);assert.equal(button(tree,'Suivant'),undefined);
    button(tree,'Nouvelle installation').props.onClick();tree=f.render(Wizard);assert.equal(nodes(tree).filter(n=>n.props?.className?.startsWith('setup-stepper-item')).length,8);assert.equal(nodes(tree).find(n=>n.props?.['aria-current']==='step').props.children[0].props.children[0],1);
    button(tree,'Retour au choix').props.onClick();tree=f.render(Wizard);assert.equal(nodes(tree).some(n=>n.props?.className==='setup-stepper'),false);assert.equal(writes,0);assert.equal(f.data.has('feedarr:setupPath'),false);
  }finally{f.close();}
});
test("return from prepared restore requires explicit cancellation and clears all sensitive state",async()=>{
  const f=fixture();let server={},deletes=0;
  f.h.request=async(path,options={})=>{if(path.endsWith('/prepare')){server={pending:{operationId:'plan'},confirmed:false};return server.pending;}if(options.method==='DELETE'){deletes++;server={};return;}return server;};
  try{
    button(f.render(Wizard),'Restaurer une configuration').props.onClick();let tree=f.render(Wizard),r=nodes(tree).find(n=>n.props?.restore).props.restore;
    r.setFile(new File(['zip'],'chosen.zip'));r.setPhrase('private phrase');r.setBootstrap('private bootstrap');r=nodes(f.render(Wizard)).find(n=>n.props?.restore).props.restore;await r.prepare();
    button(f.render(Wizard),'Retour au choix').props.onClick();tree=f.render(Wizard);assert.ok(nodes(tree).some(n=>n.props?.open===true));assert.equal(deletes,0);
    button(tree,'Rester dans la restauration').props.onClick();assert.equal(deletes,0);button(f.render(Wizard),'Retour au choix').props.onClick();await button(f.render(Wizard),'Annuler le plan et revenir au choix').props.onClick();
    tree=f.render(Wizard);assert.equal(deletes,1);assert.deepEqual(server,{});assert.equal(nodes(tree).some(n=>n.props?.className==='setup-stepper'),false);assert.ok(![...f.data.values()].some(value=>String(value).includes('private')));
    button(tree,'Restaurer une configuration').props.onClick();r=nodes(f.render(Wizard)).find(n=>n.props?.restore).props.restore;assert.equal(r.file,null);assert.equal(r.phrase,'');assert.equal(r.bootstrap,'');assert.equal(r.analysisStatus,'none');
  }finally{f.close();}
});
test("return refuses confirmed/applied operations and keeps cancellation failures visible",async()=>{
  for(const server of [{pending:{operationId:'x'},confirmed:true},{applied:{operationId:'x'}}]){
    const f=fixture();let left=false;try{f.h.request=async()=>server;await f.render(Restore,true).leave(true,()=>{left=true;});const r=f.render(Restore,true);assert.equal(left,false);assert.match(r.error,/confirmée ou appliquée/);assert.equal(r.locked,true);}finally{f.close();}
  }
  const f=fixture();let left=false;try{
    f.h.request=async(path,options={})=>{if(options.method==='DELETE')throw new Error('Annulation refusée');return {pending:{operationId:'x'}};};f.render(Restore,true);await f.effects();
    await f.render(Restore,true).leave(false,()=>{left=true;});assert.equal(left,false);assert.match(f.render(Restore,true).error,/explicitement/);
    await f.render(Restore,true).leave(true,()=>{left=true;});assert.equal(left,false);assert.equal(f.render(Restore,true).error,'Annulation refusée');assert.ok(f.render(Restore,true).state.pending);
  }finally{f.close();}
});
test("real filename/size and selection states remain accurate through replacement, analysis and errors",async()=>{
  const f=fixture();let finish,deletes=0;const render=()=>f.render(Restore,true);const view=model=>{const v=fixture();try{return v.render(RestoreView,{restore:model});}finally{v.close();}};
  try{
    let r=render();assert.equal(r.canPrepare,false);assert.match(text(view(r)),/Aucune archive sélectionnée/);
    r.setFile(new File([new Uint8Array(2048)],'my-real-backup.zip'));r=render();assert.equal(r.analysisStatus,'selected');assert.equal(r.canPrepare,true);let tree=view(r);assert.match(text(tree),/my-real-backup.zip — 2 KB/);assert.ok(nodes(tree).some(n=>n.props?.variant==='success' && n.props?.message?.includes('contenu non encore vérifié')));assert.ok(button(tree,'Remplacer le fichier'));assert.ok(button(tree,'Annuler la sélection'));
    r.setPhrase('private phrase');r=render();f.h.request=()=>new Promise(resolve=>{finish=resolve;});const pending=r.prepare();r=render();assert.equal(r.analysisStatus,'analyzing');assert.equal(r.canPrepare,false);assert.equal(r.phrase,'');finish({operationId:'old',securityChangeRequiresConfirmation:true});await pending;r=render();assert.equal(r.analysisStatus,'valid');assert.ok(nodes(view(r)).some(n=>n.props?.title==='Archive analysée et valide'));
    r.setConsent(true);r.setSecurityConsent(true);r.setPhrase('discard me');f.h.request=async(path,options)=>{assert.ok(path.endsWith('/old'));assert.equal(options.method,'DELETE');deletes++;};await render().setFile(new File(['replacement'],'replacement.zip'));r=render();assert.equal(deletes,1);assert.equal(r.state.pending,null);assert.equal(r.stage,1);assert.equal(r.analysisStatus,'selected');assert.equal(r.consent,false);assert.equal(r.securityConsent,false);assert.equal(r.phrase,'');assert.match(text(view(r)),/replacement.zip — 11 B/);
    f.h.request=async()=>{throw new Error('Archive altérée');};await r.prepare();r=render();assert.equal(r.analysisStatus,'error');assert.equal(r.error,'Archive altérée');assert.ok(nodes(view(r)).some(n=>n.props?.variant==='error' && n.props?.message?.includes('Archive altérée')));
    r.setFile({name:'too-big.zip',size:64*1024*1024+1});r=render();assert.equal(r.canPrepare,false);assert.equal(r.analysisStatus,'error');r.setFile(new File([],'empty.zip'));r=render();assert.equal(r.canPrepare,false);assert.match(text(view(r)),/empty.zip — 0 B/);
    r.setFile(null);r=render();assert.equal(r.analysisStatus,'none');assert.equal(r.canPrepare,false);assert.equal(r.error,'');assert.equal(r.file,null);assert.equal(f.data.size,0);
  }finally{f.close();}
});
test("confirmed restore reload resumes its own stepper without secrets or a fictional selected file",async()=>{
  const f=fixture();try{f.data.set('feedarr:setupRestore','true');f.h.request=async()=>({pending:{operationId:'resume'},confirmed:true});f.render(Wizard);await f.effects();const tree=f.render(Wizard);assert.equal(nodes(tree).filter(n=>n.props?.className?.startsWith('setup-stepper-item')).length,4);assert.equal(button(tree,'Retour au choix').props.disabled,true);const r=nodes(tree).find(n=>n.props?.restore).props.restore;assert.equal(r.stage,3);assert.equal(r.phrase,'');assert.equal(r.bootstrap,'');assert.equal(r.file,null);}finally{f.close();}
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

test("background restore state refresh does not erase an archive selection error", async () => {
  const f = fixture(), render = () => f.render(Restore, true);
  try {
    render().setFile(new File([], "empty.zip"));
    render().setBootstrap("changed-bootstrap"); render();
    await f.effects();
    assert.match(render().error, /vide/);
    assert.equal(render().analysisStatus, "error");
  } finally { f.close(); }
});

test("return after verified empty restore state is local even when a later state request fails", async () => {
  const f=fixture(); let reads=0;
  try {
    f.h.request=async()=>{ reads++; if(reads>1) throw new Error('Autorisation requise'); return {}; };
    f.data.set('feedarr:setupRestore','true');
    f.render(Wizard); await f.effects();
    let tree=f.render(Wizard); const restore=nodes(tree).find(n=>n.props?.restore).props.restore;
    restore.setPhrase('secret to discard');
    await button(f.render(Wizard),'Retour au choix').props.onClick(); await flush();
    tree=f.render(Wizard); assert.match(text(tree),/Bienvenue dans Feedarr/); assert.equal(reads,1);
    button(tree,'Restaurer une configuration').props.onClick();
    assert.equal(nodes(f.render(Wizard)).find(n=>n.props?.restore).props.restore.phrase,'');
  } finally { f.close(); }
});

test("an unverified local selection cannot bypass a confirmed server plan", async () => {
  const f=fixture(); let left=false, reads=0;
  try {
    f.h.request=async()=>{reads++;return {pending:{operationId:'remote'},confirmed:true};};
    f.render(Restore,true).setFile(new File(['zip'],'local.zip'));
    await f.render(Restore,true).leave(false,()=>{left=true;});
    assert.equal(left,false);assert.equal(reads,1);assert.match(f.render(Restore,true).error,/confirmée ou appliquée/);
  } finally {f.close();}
});

test("return lock and failed verification have visible explanations next to the return button", async () => {
  const f=fixture();
  try {
    f.h.request=async()=>{throw new Error('Autorisation requise');};
    f.data.set('feedarr:setupRestore','true');
    f.render(Wizard); await f.effects();
    let tree=f.render(Wizard);await button(tree,'Retour au choix').props.onClick();await flush();tree=f.render(Wizard);
    assert.equal(button(tree,'Retour au choix').props['aria-describedby'],'setup-return-status');
    assert.ok(nodes(tree).some(n=>n.props?.id==='setup-return-status' && text(n).includes('Retour au choix impossible')));
    f.h.request=async()=>({pending:{operationId:'locked'},confirmed:true});
    await nodes(tree).find(n=>n.props?.restore).props.restore.leave(false,()=>{});tree=f.render(Wizard);
    assert.equal(button(tree,'Retour au choix').props.disabled,true);
    assert.ok(nodes(tree).some(n=>n.props?.id==='setup-return-status' && text(n).includes('déjà confirmée ou appliquée')));
  } finally {f.close();}
});

test("a lost preparation response invalidates the verified empty state before returning", async () => {
  const f=fixture();let left=false;
  try {
    f.render(Restore,true);await f.effects();
    f.render(Restore,true).setFile(new File(['zip'],'configuration.zip'));
    f.h.request=async()=>{throw new Error('Connexion perdue');};
    await f.render(Restore,true).prepare();
    f.h.request=async()=>({pending:{operationId:'uncertain'},confirmed:true});
    await f.render(Restore,true).leave(false,()=>{left=true;});
    assert.equal(left,false);assert.match(f.render(Restore,true).leaveError,/confirmée ou appliquée/);
  } finally {f.close();}
});

test("fresh welcome to restore to welcome needs no bootstrap and no authorized request", async () => {
  const f=fixture(); let requests=0;
  try {
    f.h.request=async()=>{requests++;const e=new Error('Autorisation requise');e.status=403;throw e;};
    button(f.render(Wizard),'Restaurer une configuration').props.onClick();f.render(Wizard);await f.effects();
    let tree=f.render(Wizard),r=nodes(tree).find(n=>n.props?.restore).props.restore;
    assert.equal(r.file,null);assert.equal(r.error,'');assert.equal(r.checking,false);
    assert.equal(button(tree,'Retour au choix').props.disabled,false);
    button(tree,'Retour au choix').props.onClick();await flush();tree=f.render(Wizard);
    assert.match(text(tree),/Bienvenue dans Feedarr/);
    await f.effects();assert.equal(requests,0);
    assert.equal(f.data.has('feedarr:setupRestore'),false);
  } finally {f.close();}
});

test("fresh local return clears selected archive and secrets without contacting the restore API", async () => {
  const f=fixture();let requests=0;
  try {
    f.h.request=async()=>{requests++;throw new Error('Must not request');};
    button(f.render(Wizard),'Restaurer une configuration').props.onClick();
    let r=nodes(f.render(Wizard)).find(n=>n.props?.restore).props.restore;
    r.setFile(new File(['zip'],'local.zip'));r.setPhrase('private phrase');r.setBootstrap('private bootstrap');
    f.render(Wizard);await f.effects();
    button(f.render(Wizard),'Retour au choix').props.onClick();await flush();
    button(f.render(Wizard),'Restaurer une configuration').props.onClick();
    r=nodes(f.render(Wizard)).find(n=>n.props?.restore).props.restore;
    assert.equal(r.file,null);assert.equal(r.phrase,'');assert.equal(r.bootstrap,'');assert.equal(requests,0);
  } finally {f.close();}
});

test("unknown resumed states remain blocked by 401 or 403, never inferred empty", async () => {
  for(const status of [401,403]) {
    const f=fixture();let requests=0;
    try {
      f.data.set('feedarr:setupRestore','true');
      f.h.request=async()=>{requests++;const e=new Error('Autorisation requise');e.status=status;throw e;};
      f.render(Wizard);await f.effects();
      button(f.render(Wizard),'Retour au choix').props.onClick();await flush();
      const tree=f.render(Wizard);
      assert.ok(nodes(tree).some(n=>n.props?.restore));assert.equal(requests,2);
      assert.match(nodes(tree).find(n=>n.props?.restore).props.restore.leaveError,/Autorisation requise/);
    } finally {f.close();}
  }
});

test("in-flight preparation blocks return and failed preparation requires authoritative verification", async () => {
  const f=fixture();let fail,requests=0,left=false;
  try {
    let r=f.render(Restore,true);r.startFresh();r=f.render(Restore,true);
    r.setFile(new File(['zip'],'local.zip'));
    f.h.request=()=>{requests++;return new Promise((_,reject)=>{fail=reject;});};
    const preparing=f.render(Restore,true).prepare();
    r=f.render(Restore,true);assert.equal(r.busy,true);
    await r.leave(false,()=>{left=true;});assert.equal(left,false);assert.equal(requests,1);
    fail(new Error('Réponse perdue'));await preparing;
    f.h.request=async()=>{requests++;throw new Error('Autorisation requise');};
    await f.render(Restore,true).leave(false,()=>{left=true;});
    assert.equal(left,false);assert.equal(requests,2);
    assert.match(f.render(Restore,true).leaveError,/Autorisation requise/);
  } finally {f.close();}
});


test("fresh remote setup exchanges target bootstrap for a single-use token and logs in after saving", async () => {
  const f = fixture(); const saveRef = { current: null }; const calls = [];
  try {
    window.location = { protocol: "https:" };
    f.h.authState = { setupCompleted: false, authRequired: true, authConfigured: false };
    f.h.get = async () => ({ authMode: "smart", authRequired: true });
    f.h.post = async (path, body, options) => { calls.push([path, options]); return { token: "single-use" }; };
    f.h.put = async (path, body, options) => { calls.push([path, options]); const saved = { authMode: body.authMode, authConfigured: true, hasPassword: true, username: body.username }; f.h.get = async () => saved; return saved; };
    f.h.login = async (...args) => { calls.push(["login", args]); };
    const render = () => f.render(Security, { saveRef });
    render(); await f.effects(); let tree = render();
    const inputs = nodes(tree).filter(n => n.type === "input");
    inputs.find(n => n.props.type === "password" && !n.props.placeholder).props.onChange({ target: { value: "target-bootstrap" } });
    inputs.find(n => /username/i.test(n.props.placeholder || "")).props.onChange({ target: { value: "admin" } });
    for (const input of inputs.filter(n => n.props.placeholder && n.props.type === "password")) input.props.onChange({ target: { value: "correct-password" } });
    render(); await f.effects(); await saveRef.current();
    assert.deepEqual(calls, [
      ["/api/setup/bootstrap-token", { headers: { "X-Bootstrap-Secret": "target-bootstrap" } }],
      ["/api/settings/security", { headers: { "X-Feedarr-Bootstrap-Token": "single-use" } }],
      ["login", ["admin", "correct-password", false]],
    ]);
    assert.equal(nodes(render()).find(n => n.type === "input" && n.props.type === "password" && !n.props.placeholder).props.value, "");
    assert.equal(f.data.size, 0);
  } finally { f.close(); }
});

test("configured but anonymous setup offers login and return without losing the selected path", async () => {
  const f = fixture();
  try {
    f.h.authState = { setupCompleted: false, authRequired: true, authConfigured: true, authenticated: false };
    let tree = f.render(Wizard); button(tree, "Nouvelle installation").props.onClick(); tree = f.render(Wizard);
    assert.ok(nodes(tree).some(n => n.props?.state === f.h.authState && n.props.onBack));
    nodes(tree).find(n => n.props?.state === f.h.authState).props.onBack();
    assert.ok(button(f.render(Wizard), "Restaurer une configuration"));
  } finally { f.close(); }
});

test("restore restart verification sends target bootstrap and never treats a denial as empty", async () => {
  const f = fixture(); const render = () => f.render(Restore, true);
  try {
    render().setBootstrap("target-secret");
    f.h.get = async (path, options) => { assert.equal(path, "/api/setup/state"); assert.equal(options.bootstrap, "target-secret"); throw Object.assign(new Error("Autorisation requise"), { status: 401 }); };
    await render().checkRestart();
    assert.equal(render().stage, 1); assert.match(render().error, /Autorisation/);
    f.h.get = async (_path, options) => { assert.equal(options.bootstrap, "target-secret"); return { onboardingDone: true }; };
    await render().checkRestart(); assert.equal(render().stage, 4);
  } finally { f.close(); }
});


test("authenticated new installation finalizes once and clears wizard navigation for the auth gate", async () => {
  const f = fixture(); let completed = 0;
  try {
    f.h.authState = { setupCompleted: false, authRequired: true, authConfigured: true, authenticated: true };
    f.data.set("feedarr:setupPath", "install"); f.data.set("feedarr:setupStep", "8");
    f.h.post = async path => { assert.equal(path, "/api/system/onboarding/complete"); completed++; };
    const tree = f.render(Wizard);
    await nodes(tree).find(n => n.props?.onFinish).props.onFinish();
    assert.equal(completed, 1); assert.equal(f.data.get("feedarr:onboardingDone"), "true");
    assert.equal(f.data.has("feedarr:setupPath"), false); assert.equal(f.data.has("feedarr:setupStep"), false);
  } finally { f.close(); }
});


test("language authorization failure keeps settings unknown and prevents default writes", async () => {
  const f = fixture(); let writes = 0;
  try {
    f.h.get = async () => { throw Object.assign(new Error("Accès refusé"), { status: 403 }); };
    f.h.put = async () => { writes++; };
    f.render(Language); await f.effects(); const tree = f.render(Language);
    assert.equal(nodes(tree).find(n => n.type === "select").props.disabled, true);
    assert.match(text(tree), /Accès refusé/); assert.equal(writes, 0);
  } finally { f.close(); }
});

test("security already configured resumes with Next rather than invalidating the session again", async () => {
  const f = fixture(); let status;
  try {
    f.h.authState = { setupCompleted: false, authRequired: true, authConfigured: true, authenticated: true };
    f.h.get = async () => ({ authMode: "smart", authConfigured: true, hasPassword: true, username: "admin" });
    f.render(Security, { onStatusChange: value => { status = value; } }); await f.effects();
    f.render(Security, { onStatusChange: value => { status = value; } }); await f.effects();
    assert.equal(status.saved, true); assert.equal(status.ready, true);
  } finally { f.close(); }
});

test("lost prepare response can recover the persisted confirmed plan without repeating the upload", async () => {
  const f = fixture(); const render = () => f.render(Restore, true);
  try {
    render().setFile(new File(["zip"], "configuration.zip"));
    f.h.request = async () => { throw new Error("Connexion perdue"); };
    await render().prepare(); assert.match(render().error, /Connexion perdue/);
    f.h.request = async path => { assert.ok(path.endsWith("/state")); return { pending: { operationId: "persisted" }, confirmed: true }; };
    await render().reloadState(); assert.equal(render().stage, 3); assert.equal(render().canVisit(1), false); assert.equal(render().error, "");
  } finally { f.close(); }
});


test("authoritative empty restore recovery returns to file selection", async () => {
  const f = fixture(); const render = () => f.render(Restore, true);
  try {
    render().setFile(new File(["zip"], "configuration.zip"));
    f.h.request = async () => ({ operationId: "prepared" }); await render().prepare(); assert.equal(render().stage, 2);
    f.h.request = async () => ({}); await render().reloadState();
    assert.equal(render().stage, 1); assert.equal(render().state.pending, undefined); assert.ok(render().file);
  } finally { f.close(); }
});
