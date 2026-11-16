import test from "node:test";
import assert from "node:assert/strict";
import { build } from "esbuild";
import { createRequire } from "node:module";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import React, { act } from "react";
import { createDomEnvironment } from "../../test/domEnvironment.mjs";
const require = createRequire(import.meta.url);
const bundle = await build({ entryPoints:[fileURLToPath(new URL("../useRetroFetchProgress.js",import.meta.url))],bundle:true,platform:"node",format:"esm",write:false,
 plugins:[{name:"boundaries",setup(b){
 b.onResolve({filter:/^react$/},a=>({path:pathToFileURL(require.resolve(a.path)).href,external:true}));
 b.onLoad({filter:/api\/client\.js$/},()=>({contents:'export const apiGet=(...a)=>globalThis.__retroApi(...a);export const apiPost=apiGet;'}));
 b.onLoad({filter:/usePolling\.js$/},()=>({contents:'export default function usePolling(fn,ms,active){globalThis.__retroTick=active?fn:null;}'}));
 b.onLoad({filter:/taskTracker\.js$/},()=>({contents:'export function addTask(t){globalThis.__retroTracked=t;}export function removeTask(){globalThis.__retroTracked=null;}'}));
 }}]});
const dir=await mkdtemp(join(tmpdir(),"retro-batch-"));
await writeFile(join(dir,"hook.mjs"),bundle.outputFiles[0].text);
const {useRetroFetchProgress}=await import(pathToFileURL(join(dir,"hook.mjs")));
await rm(dir,{recursive:true});
for(const state of ["succeeded","failed","mixed","expired"]) test(`restored batch closes: ${state}`,async()=>{
 const env=createDomEnvironment();let root;
 try{
 const {createRoot}=await import("react-dom/client");
 window.localStorage.setItem("feedarr:retroFetch",JSON.stringify({active:true,startedAt:Date.now(),ids:[1,2],batchId:"batch-1",total:2}));
 let terminal=false;
 globalThis.__retroApi=async(url,body)=>{assert.equal(body.batchId,"batch-1");return {total:2,done:terminal?2:1,pending:terminal?0:1,terminal,state};};
 function Harness(){const {retroTask}=useRetroFetchProgress();return React.createElement("span",null,retroTask?.active?"active":"closed");}
 root=createRoot(document.getElementById("root"));await act(async()=>root.render(React.createElement(Harness)));
 await act(async()=>{globalThis.__retroTick();await new Promise(r=>setTimeout(r,0));});
 assert.equal(document.body.textContent,"active");assert.equal(JSON.parse(window.localStorage.getItem("feedarr:retroFetch")).done,1);
 await act(async()=>root.unmount());root=createRoot(document.getElementById("root"));
 await act(async()=>root.render(React.createElement(Harness)));terminal=true;
 await act(async()=>{globalThis.__retroTick();await new Promise(r=>setTimeout(r,0));});
 assert.equal(document.body.textContent,"closed");assert.equal(window.localStorage.getItem("feedarr:retroFetch"),null);assert.equal(globalThis.__retroTracked,null);
 }finally{if(root)await act(async()=>root.unmount());env.restore();}
});
