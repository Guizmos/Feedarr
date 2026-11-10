import test from "node:test";
import assert from "node:assert/strict";
import { build } from "esbuild";
import { fileURLToPath } from "node:url";

// Same lightweight hook scheduler as the existing maintenance action tests.
async function load(file) {
  const bundle = await build({ entryPoints: [fileURLToPath(new URL(file, import.meta.url))], bundle: true, platform: "node", format: "esm", write: false,
    plugins: [{ name: "controlled-ui", setup(b) {
      b.onResolve({ filter: /^react$/ }, () => ({ path: "hooks", namespace: "test" }));
      b.onResolve({ filter: /^react-router-dom$/ }, () => ({ path: "router", namespace: "test" }));
      b.onResolve({ filter: /api\/client\.js$/ }, () => ({ path: "api", namespace: "test" }));
      b.onResolve({ filter: /configurationBackupApi\.js$/ }, () => ({ path: "backup", namespace: "test" }));
      b.onResolve({ filter: /(?:InlineNotice|Step\w+|ConfigurationBackupPanel)\.jsx$/ }, args => args.importer ? ({ path: args.path, namespace: "child" }) : undefined);
      b.onLoad({ filter: /.*/, namespace: "child" }, () => ({ contents: 'export default function Child() {}' }));
      b.onLoad({ filter: /.*/, namespace: "test" }, args => ({ contents: {
        hooks: `const element=(type,props,...children)=>({type,props:{...props,children:children.flat(Infinity)}}); export default {createElement:element};
          export const useCallback=fn=>fn; export const useMemo=fn=>fn();
          export function useState(initial){const h=globalThis.__configUI,i=h.cursor++; if(!(i in h.values))h.values[i]=typeof initial==='function'?initial():initial; return [h.values[i],v=>{h.values[i]=typeof v==='function'?v(h.values[i]):v;}];}
          export function useRef(initial){const h=globalThis.__configUI,i=h.cursor++; if(!(i in h.values))h.values[i]={current:initial};return h.values[i];}
          export function useEffect(fn,deps){const h=globalThis.__configUI,i=h.cursor++,old=h.deps[i];if(!old||deps.some((d,j)=>d!==old[j])){h.deps[i]=deps;h.effects.push(fn);}}`,
        router: 'export const useNavigate=()=>globalThis.__configUI.navigate;',
        api: 'export const apiGet=(...a)=>globalThis.__configUI.get(...a); export const apiPost=(...a)=>globalThis.__configUI.post(...a);',
        backup: `export const backupRoot=initial=>initial?'/api/setup/configuration-restore':'/api/configuration-backups';
          export const configurationRequest=(...a)=>globalThis.__configUI.request(...a);
          export const configurationUpload=(file,phrase)=>({file,phrase}); export const downloadConfiguration=()=>{};
          export const resumeSetupSteps=steps=>Array.isArray(steps)?steps:null;`,
      }[args.path] }));
    } }], });
  return (await import(`data:text/javascript;base64,${Buffer.from(bundle.outputFiles[0].text).toString("base64")}`)).default;
}
const Panel = await load("./ConfigurationBackupPanel.jsx");
const Wizard = await load("../../pages/SetupWizard.jsx");
const flush = async () => { for(let i=0;i<12;i++) await Promise.resolve(); };
const nodes = tree => !tree || typeof tree !== "object" ? [] : [tree,...(tree.props?.children||[]).flatMap(nodes)];
const text = tree => typeof tree === "string" ? tree : (tree?.props?.children||[]).map(text).join("");
const button = (tree,label) => nodes(tree).find(n=>n.type==="button" && text(n)===label);
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
    inputs.find(n=>n.props.type==='file').props.onChange({target:{files:[new Blob(['zip'])]}});
    inputs.at(-1).props.onChange({target:{value:'private phrase'}});tree=f.render(Panel);
    const submit=button(tree,'Analyser la sauvegarde');const pending=submit.props.onClick();submit.props.onClick();assert.equal(calls,1);
    tree=f.render(Panel);assert.equal(nodes(tree).filter(n=>n.type==='input').at(-1).props.value,'');assert.equal(button(tree,'Analyser la sauvegarde').props.disabled,true);
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
    let tree=f.render(Wizard);assert.ok(button(tree,'Nouvelle installation'));button(tree,'Restaurer une configuration').props.onClick();tree=f.render(Wizard);assert.equal(nodes(tree).filter(n=>n.props?.initialSetup).length,1);
  }finally{f.close();}
  const resumed=fixture();try{
    resumed.data.set('feedarr:jackettConfigured','true');
    resumed.data.set('feedarr:onboardingDone','true');
    let finishOnboarding; const onboarding=new Promise(resolve=>{finishOnboarding=resolve;});
    resumed.h.get=async path=>path.endsWith('/state')?{restoreSteps:[3,8],authRequired:true,authConfigured:false}:onboarding;
    resumed.render(Wizard);await resumed.effects();const tree=resumed.render(Wizard);
    assert.deepEqual(nodes(tree).filter(n=>n.props?.className?.startsWith('setup-stepper-item')).map(n=>n.props.children[0].props.children[0]),[3,8]);
    assert.equal(nodes(tree).find(n=>n.props?.['aria-current']==='step').props.children[0].props.children[0],3);
    finishOnboarding({onboardingDone:false}); await flush();
    assert.equal(nodes(resumed.render(Wizard)).find(n=>n.props?.['aria-current']==='step').props.children[0].props.children[0],3);
    button(tree,'Resume').props.onClick();assert.ok(nodes(resumed.render(Wizard)).some(n=>n.type==='input'&&n.props.type==='password'));
  }finally{resumed.close();}
});
