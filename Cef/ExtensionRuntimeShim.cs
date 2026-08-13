namespace Skew.Cef;

/// <summary>
/// Generates the JavaScript polyfill that provides chrome.* API stubs for
/// extension content scripts and background pages. Mirrors the Mac app's
/// ExtensionRuntimeShim approach: calls are serialised as console.info
/// messages with a __SKEW_EXTENSION__ prefix, intercepted on the C# side
/// by OnConsoleMessage, and resolved by injecting __skewExtResolve back
/// into the page.
/// </summary>
internal static class ExtensionRuntimeShim
{
    /// <summary>
    /// Build the full shim JS for the given extension. Injected once per
    /// frame before any content scripts run.
    /// </summary>
    internal static string Generate(
        string extensionId, Models.ManifestMeta? manifest, string? extensionRoot = null)
    {
        var manifestJson = "{}";
        string? manifestPath = string.IsNullOrWhiteSpace(extensionRoot)
            ? null
            : Path.Combine(extensionRoot, "manifest.json");
        if (manifestPath is not null && File.Exists(manifestPath))
        {
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))
                    as System.Text.Json.Nodes.JsonObject;
                if (root is not null)
                {
                    string? defaultLocale = root["default_locale"]?.GetValue<string>();
                    foreach (string field in new[] { "name", "short_name", "description" })
                    {
                        string? value = root[field]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(value))
                            root[field] = ResolveManifestMessage(value, defaultLocale, extensionRoot!);
                    }
                    manifestJson = root.ToJsonString();
                }
            }
            catch { manifestJson = "{}"; }
        }
        else if (manifest != null)
        {
            try
            {
                manifestJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    name = manifest.Name ?? "",
                    short_name = manifest.ShortName ?? manifest.Name ?? "",
                    version = manifest.Version ?? "",
                    description = manifest.Description ?? "",
                    manifest_version = manifest.ManifestVersion
                });
            }
            catch { manifestJson = "{}"; }
        }

        string embeddedResourcesJson = BuildEmbeddedResourcesJson(manifest, extensionRoot);
        string messagesJson = BuildMessagesJson(extensionRoot);

        // The shim is a self-executing function that sets up all the chrome.*
        // polyfills and the IPC bridge. Mirrors BrowserClient.mm lines 2316–3470.
        return $@"(function(){{
var extId='{extensionId}';
var manifest={manifestJson};
var runtimeRegistry=globalThis.__skewChromeById=globalThis.__skewChromeById||{{}};
var chrome=runtimeRegistry[extId]=runtimeRegistry[extId]||{{}};
var isExtensionPage=location.protocol==='skew-extension:';
if(isExtensionPage){{globalThis.chrome=chrome;globalThis.browser=chrome;}}
else{{
  // Content scripts need to find chrome too. In Chromium they run in an
  // isolated world where the API is simply there; here they share the page's
  // global scope, and a closure variable is invisible to a bundled script that
  // checks globalThis.chrome.runtime.id before doing anything — which is the
  // guard behind the this-script-should-only-be-loaded-in-a-browser-extension
  // error that was stopping content scripts dead.
  //
  // Only filled in when the page has nothing there already, so a site with its
  // own chrome object keeps it.
  try{{
    var existing=globalThis.chrome;
    if(!existing||!existing.runtime||!existing.runtime.id){{
      globalThis.chrome=chrome;
      if(!globalThis.browser)globalThis.browser=chrome;
    }}
  }}catch(e){{}}
}}
var embeddedResources={embeddedResourcesJson};
var embeddedResourceUrls={{}};
var __skewDiagnostic=function(category,error){{
  var message='Unknown error';
  try{{message=error&&error.message?String(error.message):String(error);}}catch(e){{}}
  console.info('__SKEW_EXTENSION_DIAGNOSTIC__'+JSON.stringify({{
    extensionId:extId,category:category||'javascript',message:message.slice(0,1000)
  }}));
}};
// Content scripts normally execute in a Chromium isolated world, where page
// Trusted Types rules do not reject extension generated markup. This CEF host
// uses a scoped closure instead, so wrap only DOM elements created through the
// extension's document binding. The page's own document and prototypes remain
// unchanged.
if(!chrome.__skewDocument){{
  var trustedHtmlPolicy=null;
  try{{
    if(globalThis.trustedTypes){{
      trustedHtmlPolicy=globalThis.trustedTypes.createPolicy(
        'skew-extension-'+extId,{{createHTML:function(value){{return String(value);}}}});
    }}
  }}catch(error){{
    __skewDiagnostic('trusted-types-policy',error);
  }}
  var innerHtmlDescriptor=Object.getOwnPropertyDescriptor(Element.prototype,'innerHTML');
  function __skewWrapCreatedElement(element){{
    if(!element||element.__skewExtensionElement)return element;
    try{{Object.defineProperty(element,'__skewExtensionElement',{{value:true}});}}catch(e){{}}
    if(innerHtmlDescriptor&&innerHtmlDescriptor.get&&innerHtmlDescriptor.set){{
      try{{
        Object.defineProperty(element,'innerHTML',{{
          configurable:true,
          get:function(){{return innerHtmlDescriptor.get.call(this);}},
          set:function(value){{
            var safeValue=trustedHtmlPolicy&&typeof value==='string'
              ?trustedHtmlPolicy.createHTML(value):value;
            return innerHtmlDescriptor.set.call(this,safeValue);
          }}
        }});
      }}catch(e){{}}
    }}
    try{{
      var nativeClone=element.cloneNode.bind(element);
      Object.defineProperty(element,'cloneNode',{{configurable:true,value:function(deep){{
        return __skewWrapCreatedElement(nativeClone(!!deep));
      }}}});
    }}catch(e){{}}
    return element;
  }}
  chrome.__skewDocument=new Proxy(globalThis.document,{{
    get:function(target,property){{
      if(property==='createElement')return function(){{
        return __skewWrapCreatedElement(target.createElement.apply(target,arguments));
      }};
      if(property==='createElementNS')return function(){{
        return __skewWrapCreatedElement(target.createElementNS.apply(target,arguments));
      }};
      var value=Reflect.get(target,property,target);
      return typeof value==='function'?value.bind(target):value;
    }},
    set:function(target,property,value){{return Reflect.set(target,property,value,target);}}
  }});
}}
chrome.runtime=chrome.runtime||{{}};
var runtime=chrome.runtime;
runtime.id=extId;

// --- Event emitter factory ---
function __skewEvent(){{
  var listeners=[];
  return {{
    addListener:function(fn){{if(typeof fn==='function'&&listeners.indexOf(fn)<0)listeners.push(fn);}},
    removeListener:function(fn){{var i=listeners.indexOf(fn);if(i>=0)listeners.splice(i,1);}},
    hasListener:function(fn){{return listeners.indexOf(fn)>=0;}},
    hasListeners:function(){{return listeners.length>0;}},
    _listeners:listeners,
    _fire:function(){{var args=arguments;listeners.slice().forEach(function(fn){{try{{fn.apply(null,args);}}catch(e){{__skewDiagnostic('listener',e);}}}});}}
  }};
}}

// --- IPC bridge ---
function __skewExtCall(method,args){{
  var rid=extId+':'+Date.now()+':'+Math.random().toString(36).slice(2);
  window.__skewExtCallbacks=window.__skewExtCallbacks||{{}};
  var promise=new Promise(function(resolve,reject){{
    window.__skewExtCallbacks[rid]={{resolve:resolve,reject:reject}};
  }});
  console.info('__SKEW_EXTENSION__'+JSON.stringify({{
    requestId:rid,extensionId:extId,method:method,args:args||{{}}
  }}));
  return promise;
}}

window.__skewExtResolve=window.__skewExtResolve||function(response){{
  var cb=window.__skewExtCallbacks&&window.__skewExtCallbacks[response.requestId];
  if(!cb)return;
  if(response.deferred)return;
  delete window.__skewExtCallbacks[response.requestId];
  if(response.error)cb.reject(new Error(response.error));
  else cb.resolve(response.result);
}};

// --- chrome.runtime ---
runtime.onMessage=runtime.onMessage||__skewEvent();
runtime.onMessageExternal=runtime.onMessageExternal||__skewEvent();
runtime.onInstalled=runtime.onInstalled||__skewEvent();
runtime.onStartup=runtime.onStartup||__skewEvent();
runtime.onConnect=runtime.onConnect||__skewEvent();
runtime.OnInstalledReason=runtime.OnInstalledReason||{{INSTALL:'install',UPDATE:'update',CHROME_UPDATE:'chrome_update',SHARED_MODULE_UPDATE:'shared_module_update'}};
if(!Object.prototype.hasOwnProperty.call(runtime,'lastError'))Object.defineProperty(runtime,'lastError',{{configurable:true,get:function(){{return undefined;}}}});
runtime.getURL=function(path){{
  var clean=String(path||'').replace(/^\/+/,'');
  var embedded=embeddedResources[clean];
  if(embedded){{
    if(!embeddedResourceUrls[clean]){{
      var binary=atob(embedded.base64);
      var bytes=new Uint8Array(binary.length);
      for(var i=0;i<binary.length;i++)bytes[i]=binary.charCodeAt(i);
      embeddedResourceUrls[clean]=URL.createObjectURL(new Blob([bytes],{{type:embedded.mime}}));
    }}
    console.info('__SKEW_EXTENSION_RESOURCE__'+JSON.stringify({{extensionId:extId,path:clean}}));
    return embeddedResourceUrls[clean];
  }}
  return 'skew-extension://'+extId+'/'+encodeURI(clean);
}};
runtime.getManifest=runtime.getManifest||function(){{
  return JSON.parse(JSON.stringify(manifest));
}};
runtime.sendMessage=runtime.sendMessage||function(message,options,cb){{
  if(typeof options==='function'){{cb=options;options={{}};}}
  var p=__skewExtCall('runtime.sendMessage',{{message:message}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
runtime.setUninstallURL=runtime.setUninstallURL||function(url,cb){{
  var p=Promise.resolve(); if(typeof cb==='function')p.then(cb); return p;
}};
runtime.getPlatformInfo=runtime.getPlatformInfo||function(cb){{
  var result={{os:'win',arch:'x86-64',nacl_arch:'x86-64'}};
  var p=Promise.resolve(result); if(typeof cb==='function')p.then(cb); return p;
}};
runtime.openOptionsPage=runtime.openOptionsPage||function(cb){{
  var p=Promise.resolve(); if(typeof cb==='function')p.then(cb); return p;
}};
runtime.reload=runtime.reload||function(){{}};
runtime.connect=runtime.connect||function(){{
  var onMessage=__skewEvent(),onDisconnect=__skewEvent();
  return {{name:'',sender:undefined,onMessage:onMessage,onDisconnect:onDisconnect,
    postMessage:function(){{}},disconnect:function(){{onDisconnect._fire();}}}};
}};
if(isExtensionPage&&!chrome.__skewNativeFetchInstalled&&typeof globalThis.fetch==='function'){{
  chrome.__skewNativeFetchInstalled=true;
  var pageFetch=globalThis.fetch.bind(globalThis);
  globalThis.fetch=function(input,init){{
    var url=typeof input==='string'?input:(input&&input.url)||String(input);
    if(!/^https:\/\//i.test(url))return pageFetch(input,init);
    init=init||{{}};
    var method=String(init.method||(input&&input.method)||'GET').toUpperCase();
    var headers={{}};
    try{{new Headers(init.headers||(input&&input.headers)||{{}}).forEach(function(value,name){{
      headers[name]=value;
    }});}}catch(e){{}}
    var body=typeof init.body==='string'?init.body:null;
    return __skewExtCall('runtime.fetch',{{url:url,method:method,headers:headers,body:body}})
      .then(function(result){{
        return new Response(result.body||'',{{
          status:result.status,statusText:result.statusText||'',headers:result.headers||{{}}
        }});
      }});
  }};
}}

// Host to content page message delivery. The first listener response is sent
// back through the same authenticated request id used by the native bridge.
window.__skewExtDispatchers=window.__skewExtDispatchers||{{}};
window.__skewExtDispatchers[extId]=function(message,requestId,sourceUrl,sourceOrigin){{
  if(!runtime.onMessage)return;
  var sent=false;
  function sendResponse(value){{
    if(sent||!requestId)return; sent=true;
    console.info('__SKEW_EXTENSION_RESPONSE__'+JSON.stringify({{
      requestId:requestId,extensionId:extId,result:value
    }}));
  }}
  var sender={{id:extId,url:sourceUrl||'',origin:sourceOrigin||'',tab:{{id:0,url:sourceUrl||''}}}};
  runtime.onMessage._listeners.slice().forEach(function(listener){{
    try{{
      var returned=listener(message,sender,sendResponse);
      if(returned&&typeof returned.then==='function')returned.then(sendResponse);
    }}catch(e){{__skewDiagnostic('message-listener',e);}}
  }});
  if(requestId)setTimeout(function(){{sendResponse(null);}},1000);
}};
window.__skewExtDispatchMessage=function(targetId,message,requestId,sourceUrl,sourceOrigin){{
  var dispatch=window.__skewExtDispatchers&&window.__skewExtDispatchers[targetId];
  if(dispatch)dispatch(message,requestId,sourceUrl,sourceOrigin);
}};

// --- chrome.contextMenus ---
chrome.contextMenus=chrome.contextMenus||{{}};
chrome.contextMenus.ACTION_MENU_TOP_LEVEL_LIMIT=6;
chrome.contextMenus.onClicked=chrome.contextMenus.onClicked||__skewEvent();
chrome.contextMenus.create=chrome.contextMenus.create||function(createProperties,cb){{
  var p=__skewExtCall('contextMenus.create',{{createProperties:createProperties||{{}}}});
  if(typeof cb==='function')p.then(function(id){{cb(id);}});
  return createProperties&&createProperties.id?createProperties.id:undefined;
}};
chrome.contextMenus.update=chrome.contextMenus.update||function(id,updateProperties,cb){{
  var p=__skewExtCall('contextMenus.update',{{id:id,updateProperties:updateProperties||{{}}}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.contextMenus.remove=chrome.contextMenus.remove||function(id,cb){{
  var p=__skewExtCall('contextMenus.remove',{{id:id}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.contextMenus.removeAll=chrome.contextMenus.removeAll||function(cb){{
  var p=__skewExtCall('contextMenus.removeAll',{{}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.menus=chrome.menus||chrome.contextMenus;

// --- chrome.storage (basic local stub) ---
chrome.storage=chrome.storage||{{}};
chrome.storage.onChanged=chrome.storage.onChanged||__skewEvent();
chrome.storage.local=chrome.storage.local||{{}};
chrome.storage.local.onChanged=chrome.storage.local.onChanged||chrome.storage.onChanged;
chrome.storage.local.get=chrome.storage.local.get||function(keys,cb){{
  var p=__skewExtCall('storage.local.get',{{keys:keys}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
chrome.storage.local.set=chrome.storage.local.set||function(items,cb){{
  var p=__skewExtCall('storage.local.set',{{items:items}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.storage.local.remove=chrome.storage.local.remove||function(keys,cb){{
  var p=__skewExtCall('storage.local.remove',{{keys:keys}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.storage.local.clear=chrome.storage.local.clear||function(cb){{
  var p=__skewExtCall('storage.local.clear',{{}});
  if(typeof cb==='function')p.then(function(){{cb();}});
  return p;
}};
chrome.storage.local.getBytesInUse=chrome.storage.local.getBytesInUse||function(keys,cb){{
  var p=__skewExtCall('storage.local.getBytesInUse',{{keys:keys}});if(typeof cb==='function')p.then(cb);return p;
}};
chrome.storage.sync=chrome.storage.sync||chrome.storage.local;
chrome.storage.session=chrome.storage.session||chrome.storage.local;
chrome.storage.managed=chrome.storage.managed||{{}};
chrome.storage.managed.get=chrome.storage.managed.get||function(keys,cb){{var p=Promise.resolve({{}});if(typeof cb==='function')p.then(cb);return p;}};
chrome.storage.managed.getBytesInUse=chrome.storage.managed.getBytesInUse||function(keys,cb){{var p=Promise.resolve(0);if(typeof cb==='function')p.then(cb);return p;}};

// --- chrome.tabs (minimal stubs) ---
chrome.tabs=chrome.tabs||{{}};
chrome.tabs.TAB_ID_NONE=-1;
chrome.tabs.onActivated=chrome.tabs.onActivated||__skewEvent();
chrome.tabs.onCreated=chrome.tabs.onCreated||__skewEvent();
chrome.tabs.onRemoved=chrome.tabs.onRemoved||__skewEvent();
chrome.tabs.onReplaced=chrome.tabs.onReplaced||__skewEvent();
chrome.tabs.onUpdated=chrome.tabs.onUpdated||__skewEvent();
chrome.tabs.query=chrome.tabs.query||function(queryInfo,cb){{
  var p=__skewExtCall('tabs.query',{{queryInfo:queryInfo||{{}}}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
chrome.tabs.create=chrome.tabs.create||function(createProperties,cb){{
  var p=__skewExtCall('tabs.create',{{createProperties:createProperties||{{}}}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
chrome.tabs.sendMessage=chrome.tabs.sendMessage||function(tabId,message,options,cb){{
  if(typeof options==='function'){{cb=options;options={{}};}}
  var p=__skewExtCall('tabs.sendMessage',{{tabId:tabId,message:message}});
  if(typeof cb==='function')p.then(cb);
  return p;
}};
chrome.tabs.reload=chrome.tabs.reload||function(tabId,reloadProperties,cb){{
  if(typeof tabId==='object'||typeof tabId==='function'){{cb=reloadProperties;reloadProperties=tabId;tabId=null;}}
  var p=__skewExtCall('tabs.reload',{{tabId:tabId,reloadProperties:reloadProperties||{{}}}});
  if(typeof cb==='function')p.then(function(){{cb();}}); return p;
}};
chrome.tabs.detectLanguage=chrome.tabs.detectLanguage||function(tabId,cb){{
  if(typeof tabId==='function'){{cb=tabId;tabId=null;}}
  var p=__skewExtCall('tabs.detectLanguage',{{tabId:tabId}});
  if(typeof cb==='function')p.then(cb); return p;
}};
chrome.tabs.get=chrome.tabs.get||function(tabId,cb){{
  var p=__skewExtCall('tabs.get',{{tabId:tabId}});
  if(typeof cb==='function')p.then(cb); return p;
}};
chrome.tabs.getCurrent=chrome.tabs.getCurrent||function(cb){{
  var p=chrome.tabs.query({{active:true,currentWindow:true}}).then(function(tabs){{return tabs[0]||null;}});
  if(typeof cb==='function')p.then(cb); return p;
}};
// Navigating and activating are real now, so this goes to the host rather than
// resolving a made-up tab object the caller then acts on.
chrome.tabs.update=chrome.tabs.update||function(tabId,updateProperties,cb){{
  if(typeof tabId==='object'){{cb=updateProperties;updateProperties=tabId;tabId=null;}}
  var p=__skewExtCall('tabs.update',{{tabId:tabId,updateProperties:updateProperties||{{}}}});
  if(typeof cb==='function')p.then(cb); return p;
}};
chrome.tabs.executeScript=chrome.tabs.executeScript||function(tabId,details,cb){{
  if(typeof tabId==='object'){{cb=details;details=tabId;tabId=null;}}
  var injection={{target:{{tabId:tabId||0,allFrames:!!(details&&details.allFrames)}},
    files:details&&details.file?[details.file]:[]}};
  var p=chrome.scripting&&chrome.scripting.executeScript
    ?chrome.scripting.executeScript(injection):Promise.resolve([]);
  if(typeof cb==='function')p.then(cb); return p;
}};
chrome.tabs.insertCSS=chrome.tabs.insertCSS||function(tabId,details,cb){{
  if(typeof tabId==='object'){{cb=details;details=tabId;tabId=null;}}
  var injection={{target:{{tabId:tabId||0,allFrames:!!(details&&details.allFrames)}},files:details&&details.file?[details.file]:[],css:details&&details.code||''}};
  var p=chrome.scripting.insertCSS(injection);if(typeof cb==='function')p.then(function(){{cb();}});return p;
}};
chrome.tabs.removeCSS=chrome.tabs.removeCSS||function(tabId,details,cb){{
  if(typeof tabId==='object'){{cb=details;details=tabId;tabId=null;}}
  var injection={{target:{{tabId:tabId||0,allFrames:!!(details&&details.allFrames)}},files:details&&details.file?[details.file]:[],css:details&&details.code||''}};
  var p=chrome.scripting.removeCSS(injection);if(typeof cb==='function')p.then(function(){{cb();}});return p;
}};
chrome.tabs.remove=chrome.tabs.remove||function(tabIds,cb){{var p=__skewExtCall('tabs.remove',{{tabIds:tabIds}});if(typeof cb==='function')p.then(function(){{cb();}});return p;}};

// --- Common lifecycle APIs used while modern extensions initialise ---
chrome.alarms=chrome.alarms||{{}};
chrome.alarms.onAlarm=chrome.alarms.onAlarm||__skewEvent();
var __skewAlarms=chrome.alarms.__skewAlarms=chrome.alarms.__skewAlarms||{{}};
chrome.alarms.create=chrome.alarms.create||function(name,info){{
  if(typeof name==='object'){{info=name;name='';}}info=info||{{}};name=String(name||'');
  if(__skewAlarms[name]&&__skewAlarms[name].timer)clearTimeout(__skewAlarms[name].timer);
  var delay=Math.max(0,info.when?info.when-Date.now():(info.delayInMinutes||0)*60000);
  var alarm={{name:name,scheduledTime:Date.now()+delay,periodInMinutes:info.periodInMinutes}};
  function fire(){{alarm.scheduledTime=Date.now();chrome.alarms.onAlarm._fire(alarm);if(info.periodInMinutes)alarm.timer=setTimeout(fire,info.periodInMinutes*60000);else delete __skewAlarms[name];}}
  alarm.timer=setTimeout(fire,delay);__skewAlarms[name]=alarm;
}};
chrome.alarms.clear=chrome.alarms.clear||function(name,cb){{var existed=!!__skewAlarms[name];if(existed){{clearTimeout(__skewAlarms[name].timer);delete __skewAlarms[name];}}var p=Promise.resolve(existed);if(cb)p.then(cb);return p;}};
chrome.alarms.clearAll=chrome.alarms.clearAll||function(cb){{var names=Object.keys(__skewAlarms),existed=names.length>0;names.forEach(function(name){{clearTimeout(__skewAlarms[name].timer);delete __skewAlarms[name];}});var p=Promise.resolve(existed);if(cb)p.then(cb);return p;}};
chrome.alarms.get=chrome.alarms.get||function(name,cb){{var alarm=__skewAlarms[name]||null;var p=Promise.resolve(alarm&&{{name:alarm.name,scheduledTime:alarm.scheduledTime,periodInMinutes:alarm.periodInMinutes}});if(cb)p.then(cb);return p;}};
chrome.alarms.getAll=chrome.alarms.getAll||function(cb){{var result=Object.keys(__skewAlarms).map(function(name){{var a=__skewAlarms[name];return {{name:a.name,scheduledTime:a.scheduledTime,periodInMinutes:a.periodInMinutes}};}});var p=Promise.resolve(result);if(cb)p.then(cb);return p;}};

chrome.idle=chrome.idle||{{}};
chrome.idle.onStateChanged=chrome.idle.onStateChanged||__skewEvent();
chrome.idle.queryState=chrome.idle.queryState||function(seconds,cb){{var p=Promise.resolve('active');if(cb)p.then(cb);return p;}};

chrome.windows=chrome.windows||{{}};
chrome.windows.onRemoved=chrome.windows.onRemoved||__skewEvent();
chrome.windows.update=chrome.windows.update||function(id,info,cb){{var p=Promise.resolve({{id:id,focused:true}});if(cb)p.then(cb);return p;}};

chrome.webNavigation=chrome.webNavigation||{{}};
chrome.webNavigation.onBeforeNavigate=chrome.webNavigation.onBeforeNavigate||__skewEvent();
chrome.webNavigation.onCommitted=chrome.webNavigation.onCommitted||__skewEvent();
chrome.webNavigation.onCompleted=chrome.webNavigation.onCompleted||__skewEvent();
chrome.webNavigation.onDOMContentLoaded=chrome.webNavigation.onDOMContentLoaded||__skewEvent();
chrome.webNavigation.onCreatedNavigationTarget=chrome.webNavigation.onCreatedNavigationTarget||__skewEvent();
chrome.webNavigation.onErrorOccurred=chrome.webNavigation.onErrorOccurred||__skewEvent();
chrome.webNavigation.onHistoryStateUpdated=chrome.webNavigation.onHistoryStateUpdated||__skewEvent();
chrome.webNavigation.onReferenceFragmentUpdated=chrome.webNavigation.onReferenceFragmentUpdated||__skewEvent();
chrome.webNavigation.onTabReplaced=chrome.webNavigation.onTabReplaced||__skewEvent();
chrome.webNavigation.patchedForOnHistoryStateUpdated=true;
chrome.webNavigation.getAllFrames=chrome.webNavigation.getAllFrames||function(details,cb){{
  var p=chrome.tabs.query({{active:true,currentWindow:true}}).then(function(tabs){{
    var tab=tabs&&tabs[0];
    return [{{frameId:0,parentFrameId:-1,url:tab&&tab.url?tab.url:'about:blank'}}];
  }});if(cb)p.then(cb);return p;
}};

chrome.webRequest=chrome.webRequest||{{}};
chrome.webRequest.ResourceType=chrome.webRequest.ResourceType||{{
  MAIN_FRAME:'main_frame',SUB_FRAME:'sub_frame',STYLESHEET:'stylesheet',SCRIPT:'script',IMAGE:'image',
  FONT:'font',OBJECT:'object',XMLHTTPREQUEST:'xmlhttprequest',PING:'ping',CSP_REPORT:'csp_report',
  MEDIA:'media',WEBSOCKET:'websocket',WEBTRANSPORT:'webtransport',WEBBUNDLE:'webbundle',OTHER:'other'
}};
chrome.webRequest.OnBeforeRequestOptions=chrome.webRequest.OnBeforeRequestOptions||{{BLOCKING:'blocking',REQUEST_BODY:'requestBody',EXTRA_HEADERS:'extraHeaders'}};
chrome.webRequest.OnBeforeSendHeadersOptions=chrome.webRequest.OnBeforeSendHeadersOptions||{{REQUEST_HEADERS:'requestHeaders',BLOCKING:'blocking',EXTRA_HEADERS:'extraHeaders'}};
chrome.webRequest.OnSendHeadersOptions=chrome.webRequest.OnSendHeadersOptions||{{REQUEST_HEADERS:'requestHeaders',EXTRA_HEADERS:'extraHeaders'}};
chrome.webRequest.OnHeadersReceivedOptions=chrome.webRequest.OnHeadersReceivedOptions||{{RESPONSE_HEADERS:'responseHeaders',BLOCKING:'blocking',EXTRA_HEADERS:'extraHeaders'}};
chrome.webRequest.OnCompletedOptions=chrome.webRequest.OnCompletedOptions||{{RESPONSE_HEADERS:'responseHeaders',EXTRA_HEADERS:'extraHeaders'}};
chrome.webRequest.OnAuthRequiredOptions=chrome.webRequest.OnAuthRequiredOptions||{{RESPONSE_HEADERS:'responseHeaders',BLOCKING:'blocking',ASYNC_BLOCKING:'asyncBlocking',EXTRA_HEADERS:'extraHeaders'}};
chrome.webRequest.onBeforeRequest=chrome.webRequest.onBeforeRequest||__skewEvent();
chrome.webRequest.onBeforeSendHeaders=chrome.webRequest.onBeforeSendHeaders||__skewEvent();
chrome.webRequest.onSendHeaders=chrome.webRequest.onSendHeaders||__skewEvent();
chrome.webRequest.onHeadersReceived=chrome.webRequest.onHeadersReceived||__skewEvent();
chrome.webRequest.onAuthRequired=chrome.webRequest.onAuthRequired||__skewEvent();
chrome.webRequest.onBeforeRedirect=chrome.webRequest.onBeforeRedirect||__skewEvent();
chrome.webRequest.onResponseStarted=chrome.webRequest.onResponseStarted||__skewEvent();
chrome.webRequest.onCompleted=chrome.webRequest.onCompleted||__skewEvent();
chrome.webRequest.onErrorOccurred=chrome.webRequest.onErrorOccurred||__skewEvent();
chrome.webRequest.handlerBehaviorChanged=chrome.webRequest.handlerBehaviorChanged||function(cb){{var p=Promise.resolve();if(cb)p.then(cb);return p;}};

chrome.notifications=chrome.notifications||{{}};
chrome.notifications.onClicked=chrome.notifications.onClicked||__skewEvent();
chrome.notifications.onButtonClicked=chrome.notifications.onButtonClicked||__skewEvent();
chrome.notifications.create=chrome.notifications.create||function(id,options,cb){{
  if(typeof id==='object'){{cb=options;options=id;id='';}}var p=Promise.resolve(id||'');if(cb)p.then(cb);return p;
}};

chrome.declarativeNetRequest=chrome.declarativeNetRequest||{{}};
chrome.declarativeNetRequest.MAX_NUMBER_OF_DYNAMIC_AND_SESSION_RULES=chrome.declarativeNetRequest.MAX_NUMBER_OF_DYNAMIC_AND_SESSION_RULES||5000;
chrome.declarativeNetRequest.MAX_NUMBER_OF_DYNAMIC_RULES=chrome.declarativeNetRequest.MAX_NUMBER_OF_DYNAMIC_RULES||5000;
chrome.declarativeNetRequest.MAX_NUMBER_OF_ENABLED_STATIC_RULESETS=chrome.declarativeNetRequest.MAX_NUMBER_OF_ENABLED_STATIC_RULESETS||50;
// Dynamic and session rules go to the host engine, which is what actually
// blocks — a blocker that adds rules at runtime (per-site toggles, temporary
// allowances) would otherwise be writing into a stub that answers nothing.
chrome.declarativeNetRequest.getDynamicRules=chrome.declarativeNetRequest.getDynamicRules||function(cb){{
  var p=__skewExtCall('declarativeNetRequest.getDynamicRules',{{}}).then(function(r){{return r||[];}});
  if(cb)p.then(cb);return p;
}};
chrome.declarativeNetRequest.getSessionRules=chrome.declarativeNetRequest.getSessionRules||function(cb){{
  var p=__skewExtCall('declarativeNetRequest.getSessionRules',{{}}).then(function(r){{return r||[];}});
  if(cb)p.then(cb);return p;
}};
chrome.declarativeNetRequest.getEnabledRulesets=chrome.declarativeNetRequest.getEnabledRulesets||function(cb){{var p=Promise.resolve([]);if(cb)p.then(cb);return p;}};
chrome.declarativeNetRequest.getAvailableStaticRuleCount=chrome.declarativeNetRequest.getAvailableStaticRuleCount||function(cb){{var p=Promise.resolve(30000);if(cb)p.then(cb);return p;}};
chrome.declarativeNetRequest.getDisabledRuleIds=chrome.declarativeNetRequest.getDisabledRuleIds||function(options,cb){{var p=Promise.resolve([]);if(cb)p.then(cb);return p;}};
chrome.declarativeNetRequest.isRegexSupported=chrome.declarativeNetRequest.isRegexSupported||function(options,cb){{var p=Promise.resolve({{isSupported:true}});if(cb)p.then(cb);return p;}};
['updateDynamicRules','updateSessionRules'].forEach(function(name){{
  chrome.declarativeNetRequest[name]=chrome.declarativeNetRequest[name]||function(options,cb){{
    var p=__skewExtCall('declarativeNetRequest.'+name,options||{{}});
    if(cb)p.then(function(){{cb();}});
    return p;
  }};
}});
// Static ruleset enablement is read from the manifest at load, so these accept
// the call and report success rather than pretending to reconfigure anything.
['updateEnabledRulesets','updateStaticRules'].forEach(function(name){{
  chrome.declarativeNetRequest[name]=chrome.declarativeNetRequest[name]||function(options,cb){{var p=Promise.resolve();if(cb)p.then(cb);return p;}};
}});
chrome.declarativeNetRequest.onRuleMatchedDebug=chrome.declarativeNetRequest.onRuleMatchedDebug||__skewEvent();

// --- chrome.action, browserAction, scripting and commands ---
chrome.action=chrome.action||{{}};
chrome.action.onClicked=chrome.action.onClicked||__skewEvent();
var __skewActionState=chrome.action.__skewState=chrome.action.__skewState||{{
  popup:(manifest.action&&manifest.action.default_popup)||(manifest.browser_action&&manifest.browser_action.default_popup)||'',
  title:(manifest.action&&manifest.action.default_title)||(manifest.browser_action&&manifest.browser_action.default_title)||'',
  badgeText:'',badgeBackgroundColor:null,enabled:true
}};
chrome.action.getPopup=chrome.action.getPopup||function(details,cb){{var p=Promise.resolve(__skewActionState.popup);if(typeof cb==='function')p.then(cb);return p;}};
chrome.action.setPopup=chrome.action.setPopup||function(details,cb){{__skewActionState.popup=details&&details.popup||'';var p=Promise.resolve();if(typeof cb==='function')p.then(cb);return p;}};
chrome.action.getTitle=chrome.action.getTitle||function(details,cb){{var p=Promise.resolve(__skewActionState.title);if(typeof cb==='function')p.then(cb);return p;}};
chrome.action.getBadgeText=chrome.action.getBadgeText||function(details,cb){{var p=Promise.resolve(__skewActionState.badgeText);if(typeof cb==='function')p.then(cb);return p;}};
chrome.action.getBadgeBackgroundColor=chrome.action.getBadgeBackgroundColor||function(details,cb){{var p=Promise.resolve(__skewActionState.badgeBackgroundColor);if(typeof cb==='function')p.then(cb);return p;}};
['setTitle','setIcon','setBadgeText','setBadgeBackgroundColor','enable','disable'].forEach(function(name){{
  chrome.action[name]=chrome.action[name]||function(details,cb){{
    if(name==='setTitle')__skewActionState.title=details&&details.title||'';
    if(name==='setBadgeText')__skewActionState.badgeText=details&&details.text||'';
    if(name==='setBadgeBackgroundColor')__skewActionState.badgeBackgroundColor=details&&details.color||null;
    if(name==='enable')__skewActionState.enabled=true;
    if(name==='disable')__skewActionState.enabled=false;
    var p=Promise.resolve(); if(typeof cb==='function')p.then(cb); return p;
  }};
}});
chrome.browserAction=chrome.browserAction||chrome.action;

chrome.scripting=chrome.scripting||{{}};
chrome.scripting.ExecutionWorld=chrome.scripting.ExecutionWorld||{{ISOLATED:'ISOLATED',MAIN:'MAIN'}};
chrome.scripting.executeScript=chrome.scripting.executeScript||function(injection,cb){{
  var p=__skewExtCall('scripting.executeScript',{{injection:injection||{{}}}});
  if(typeof cb==='function')p.then(cb); return p;
}};
chrome.scripting.insertCSS=chrome.scripting.insertCSS||function(injection,cb){{var p=__skewExtCall('scripting.insertCSS',{{injection:injection||{{}}}});if(typeof cb==='function')p.then(function(){{cb();}});return p;}};
chrome.scripting.removeCSS=chrome.scripting.removeCSS||function(injection,cb){{var p=__skewExtCall('scripting.removeCSS',{{injection:injection||{{}}}});if(typeof cb==='function')p.then(function(){{cb();}});return p;}};

chrome.permissions=chrome.permissions||{{}};
chrome.permissions.getAll=chrome.permissions.getAll||function(cb){{var result={{permissions:(manifest.permissions||[]).slice(),origins:(manifest.host_permissions||[]).slice()}};var p=Promise.resolve(result);if(cb)p.then(cb);return p;}};
chrome.permissions.contains=chrome.permissions.contains||function(request,cb){{request=request||{{}};var declared=(manifest.permissions||[]).concat(manifest.optional_permissions||[]),origins=(manifest.host_permissions||[]).concat(manifest.optional_host_permissions||[]);var value=(request.permissions||[]).every(function(x){{return declared.indexOf(x)>=0;}})&&(request.origins||[]).every(function(x){{return origins.indexOf(x)>=0;}});var p=Promise.resolve(value);if(cb)p.then(cb);return p;}};
chrome.permissions.request=chrome.permissions.request||function(request,cb){{var p=chrome.permissions.contains(request||{{}});if(cb)p.then(cb);return p;}};
chrome.permissions.remove=chrome.permissions.remove||function(request,cb){{var p=Promise.resolve(false);if(cb)p.then(cb);return p;}};

chrome.management=chrome.management||{{}};
function __skewManagementSelf(){{return {{id:extId,name:manifest.name||'',shortName:manifest.short_name||manifest.name||'',description:manifest.description||'',version:manifest.version||'',enabled:true,mayDisable:true,type:'extension',installType:'normal',homepageUrl:manifest.homepage_url||'',optionsUrl:manifest.options_ui&&manifest.options_ui.page?runtime.getURL(manifest.options_ui.page):''}};}}
chrome.management.getSelf=chrome.management.getSelf||function(cb){{var p=Promise.resolve(__skewManagementSelf());if(cb)p.then(cb);return p;}};
chrome.management.getAll=chrome.management.getAll||function(cb){{var p=Promise.resolve([__skewManagementSelf()]);if(cb)p.then(cb);return p;}};
chrome.management.get=chrome.management.get||function(id,cb){{var p=Promise.resolve(__skewManagementSelf());if(cb)p.then(cb);return p;}};

chrome.dom=chrome.dom||{{}};
chrome.dom.openOrClosedShadowRoot=chrome.dom.openOrClosedShadowRoot||function(element){{return element&&element.shadowRoot||null;}};

chrome.devtools=chrome.devtools||{{}};
chrome.devtools.inspectedWindow=chrome.devtools.inspectedWindow||{{tabId:0,reload:function(){{}}}};
chrome.devtools.panels=chrome.devtools.panels||{{themeName:'dark',openResource:function(){{}},create:function(title,icon,page,cb){{var panel={{onShown:__skewEvent(),onHidden:__skewEvent(),onSearch:__skewEvent()}};if(cb)cb(panel);return Promise.resolve(panel);}}}};

chrome.commands=chrome.commands||{{}};
chrome.commands.onCommand=chrome.commands.onCommand||__skewEvent();

// --- chrome.cookies ---
chrome.cookies=chrome.cookies||{{}};
chrome.cookies.onChanged=chrome.cookies.onChanged||__skewEvent();
chrome.cookies.get=chrome.cookies.get||function(details,cb){{
  var p=__skewExtCall('cookies.get',{{details:details||{{}}}});if(typeof cb==='function')p.then(cb);return p;
}};
chrome.cookies.getAll=chrome.cookies.getAll||function(details,cb){{
  if(typeof details==='function'){{cb=details;details={{}};}}
  var p=__skewExtCall('cookies.getAll',{{details:details||{{}}}}).then(function(r){{return r||[];}});
  if(typeof cb==='function')p.then(cb);return p;
}};
chrome.cookies.set=chrome.cookies.set||function(details,cb){{
  var p=__skewExtCall('cookies.set',{{details:details||{{}}}});if(typeof cb==='function')p.then(cb);return p;
}};
chrome.cookies.remove=chrome.cookies.remove||function(details,cb){{
  var p=__skewExtCall('cookies.remove',{{details:details||{{}}}});if(typeof cb==='function')p.then(cb);return p;
}};
chrome.cookies.getAllCookieStores=chrome.cookies.getAllCookieStores||function(cb){{
  var p=Promise.resolve([{{id:'0',tabIds:[]}}]);if(typeof cb==='function')p.then(cb);return p;
}};

// --- chrome.downloads ---
chrome.downloads=chrome.downloads||{{}};
chrome.downloads.onCreated=chrome.downloads.onCreated||__skewEvent();
chrome.downloads.onChanged=chrome.downloads.onChanged||__skewEvent();
chrome.downloads.onDeterminingFilename=chrome.downloads.onDeterminingFilename||__skewEvent();
chrome.downloads.download=chrome.downloads.download||function(options,cb){{
  var p=__skewExtCall('downloads.download',{{options:options||{{}}}});
  if(typeof cb==='function')p.then(cb);return p;
}};
chrome.downloads.search=chrome.downloads.search||function(query,cb){{
  if(typeof query==='function'){{cb=query;query={{}};}}
  var p=__skewExtCall('downloads.search',{{query:query||{{}}}}).then(function(r){{return r||[];}});
  if(typeof cb==='function')p.then(cb);return p;
}};
chrome.downloads.cancel=chrome.downloads.cancel||function(id,cb){{
  var p=__skewExtCall('downloads.cancel',{{id:id}});if(typeof cb==='function')p.then(function(){{cb();}});return p;
}};
chrome.downloads.show=chrome.downloads.show||function(id){{__skewExtCall('downloads.show',{{id:id}});}};
chrome.downloads.showDefaultFolder=chrome.downloads.showDefaultFolder||function(){{__skewExtCall('downloads.show',{{}});}};

// --- chrome.i18n ---
// The extension's own messages, read from _locales at load. Returning the key
// — which is what this did — leaves __MSG_appName__ on screen wherever an
// extension localises its own strings, and localised extensions are the norm.
var __skewMessages={messagesJson};
chrome.i18n=chrome.i18n||{{}};
chrome.i18n.getUILanguage=chrome.i18n.getUILanguage||function(){{return 'en';}};
chrome.i18n.getAcceptLanguages=chrome.i18n.getAcceptLanguages||function(cb){{
  var p=Promise.resolve(['en-US','en']);if(typeof cb==='function')p.then(cb);return p;
}};
chrome.i18n.getMessage=chrome.i18n.getMessage||function(name,substitutions){{
  if(name==='@@extension_id')return extId;
  if(name==='@@ui_locale')return 'en';
  if(name==='@@bidi_dir')return 'ltr';
  if(name==='@@bidi_reversed_dir')return 'rtl';
  if(name==='@@bidi_start_edge')return 'left';
  if(name==='@@bidi_end_edge')return 'right';
  var key=String(name||'');
  var entry=__skewMessages[key];
  if(!entry){{
    // Message names are case-insensitive in Chrome.
    var lower=key.toLowerCase();
    for(var candidate in __skewMessages){{
      if(candidate.toLowerCase()===lower){{entry=__skewMessages[candidate];break;}}
    }}
  }}
  if(!entry)return '';
  var text=String(entry.message||'');
  // Named placeholders first — their content is what the numbered
  // substitutions actually land in.
  if(entry.placeholders){{
    for(var placeholder in entry.placeholders){{
      var definition=entry.placeholders[placeholder];
      var content=definition&&definition.content!=null?String(definition.content):'';
      text=text.replace(new RegExp('\\\\$'+placeholder+'\\\\$','gi'),content);
    }}
  }}
  var values=substitutions==null?[]:(Array.isArray(substitutions)?substitutions:[substitutions]);
  for(var i=0;i<9;i++){{
    var value=i<values.length?String(values[i]):'';
    text=text.split('$'+(i+1)).join(value);
  }}
  return text.split('$$').join('$');
}};

// --- chrome.extension ---
chrome.extension=chrome.extension||{{}};
chrome.extension.getURL=chrome.extension.getURL||runtime.getURL;

// --- globalThis.browser mirror ---
if(isExtensionPage){{globalThis.browser=chrome;}}

}})();";
    }

    /// <summary>
    /// The extension's messages.json for the locale it will be read in, verbatim
    /// so placeholders survive. Falls back through the declared default locale
    /// to English, then to whatever single locale the extension ships.
    /// </summary>
    private static string BuildMessagesJson(string? extensionRoot)
    {
        if (string.IsNullOrWhiteSpace(extensionRoot)) return "{}";
        string localesRoot = Path.Combine(extensionRoot, "_locales");
        if (!Directory.Exists(localesRoot)) return "{}";

        string? defaultLocale = null;
        try
        {
            string manifestPath = Path.Combine(extensionRoot, "manifest.json");
            if (File.Exists(manifestPath))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (document.RootElement.TryGetProperty("default_locale", out var value))
                    defaultLocale = value.GetString();
            }
        }
        catch (Exception) { }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(defaultLocale)) candidates.Add(defaultLocale.Replace('-', '_'));
        candidates.Add("en");
        candidates.Add("en_US");

        foreach (string locale in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(localesRoot, locale, "messages.json");
            if (File.Exists(path)) return ReadMessages(path);
        }

        // Some extensions ship exactly one locale and name it something else.
        string? only = Directory.EnumerateDirectories(localesRoot)
            .Select(directory => Path.Combine(directory, "messages.json"))
            .FirstOrDefault(File.Exists);
        return only is null ? "{}" : ReadMessages(only);

        static string ReadMessages(string path)
        {
            try
            {
                string text = File.ReadAllText(path);
                // Parsed and re-emitted so a malformed file cannot inject
                // anything into the shim it is embedded in.
                using var document = System.Text.Json.JsonDocument.Parse(text);
                return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? document.RootElement.GetRawText() : "{}";
            }
            catch (Exception)
            {
                return "{}";
            }
        }
    }

    private static string BuildEmbeddedResourcesJson(
        Models.ManifestMeta? manifest, string? extensionRoot)
    {
        if (manifest?.WebAccessibleResources is not { } resourcesElement ||
            resourcesElement.ValueKind != System.Text.Json.JsonValueKind.Array ||
            string.IsNullOrWhiteSpace(extensionRoot) ||
            !Directory.Exists(extensionRoot))
            return "{}";

        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in resourcesElement.EnumerateArray())
        {
            if (entry.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                AddPattern(entry.GetString());
            }
            else if (entry.ValueKind == System.Text.Json.JsonValueKind.Object &&
                entry.TryGetProperty("resources", out var declared) &&
                declared.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in declared.EnumerateArray())
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                        AddPattern(item.GetString());
            }
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(extensionRoot);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        foreach (string pattern in patterns)
        {
            if (pattern.IndexOfAny(['*', '?']) >= 0)
            {
                foreach (string candidate in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(root, candidate).Replace('\\', '/');
                    if (ResourcePatternMatches(pattern, relative)) files.Add(relative);
                }
            }
            else
            {
                string relative = pattern.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                string full = Path.GetFullPath(Path.Combine(root, relative));
                if (full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                    files.Add(Path.GetRelativePath(root, full).Replace('\\', '/'));
            }
        }

        const long maxResourceBytes = 1024 * 1024;
        const long maxTotalBytes = 2 * 1024 * 1024;
        long totalBytes = 0;
        var embedded = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var info = new FileInfo(full);
            if (info.Length > maxResourceBytes || totalBytes + info.Length > maxTotalBytes) continue;
            string? mime = TextResourceMimeType(Path.GetExtension(full));
            if (mime is null) continue;
            embedded[relative] = new Dictionary<string, string>
            {
                ["mime"] = mime,
                ["base64"] = Convert.ToBase64String(File.ReadAllBytes(full))
            };
            totalBytes += info.Length;
        }

        return System.Text.Json.JsonSerializer.Serialize(embedded);

        void AddPattern(string? value)
        {
            string normalized = (value ?? "").Trim().TrimStart('/').Replace('\\', '/');
            if (!string.IsNullOrEmpty(normalized) && !normalized.Contains("..", StringComparison.Ordinal))
                patterns.Add(normalized);
        }
    }

    private static bool ResourcePatternMatches(string pattern, string relativePath)
    {
        string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            relativePath, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Resolve a __MSG_key__ manifest string against the extension's locales.
    /// Anything else comes back unchanged, so this is safe to call on every
    /// name and description.
    /// </summary>
    internal static string LocalizeManifestString(string? value, string extensionRoot)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;
        string? defaultLocale = null;
        try
        {
            string manifestPath = Path.Combine(extensionRoot, "manifest.json");
            if (File.Exists(manifestPath))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (document.RootElement.TryGetProperty("default_locale", out var locale))
                    defaultLocale = locale.GetString();
            }
        }
        catch (Exception) { }
        return ResolveManifestMessage(value, defaultLocale, extensionRoot);
    }

    private static string ResolveManifestMessage(string value, string? defaultLocale, string root)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            value ?? string.Empty, "^__MSG_(.+)__$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return value ?? string.Empty;

        string key = match.Groups[1].Value;
        foreach (string locale in new[] { defaultLocale, "en", "en_US" }
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Replace('-', '_'))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(root, "_locales", locale, "messages.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty(key, out var entry) &&
                    entry.TryGetProperty("message", out var message))
                    return message.GetString() ?? value ?? string.Empty;
            }
            catch (System.Text.Json.JsonException) { }
        }
        return value ?? string.Empty;
    }

    private static string? TextResourceMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".css" => "text/css;charset=utf-8",
        ".js" => "text/javascript;charset=utf-8",
        ".json" => "application/json;charset=utf-8",
        ".html" or ".htm" => "text/html;charset=utf-8",
        ".txt" => "text/plain;charset=utf-8",
        ".svg" => "image/svg+xml;charset=utf-8",
        ".xml" => "application/xml;charset=utf-8",
        _ => null
    };
}
