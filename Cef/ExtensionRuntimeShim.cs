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
        if (manifest != null)
        {
            try
            {
                manifestJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    name = manifest.Name ?? "",
                    version = manifest.Version ?? "",
                    description = manifest.Description ?? "",
                    manifest_version = manifest.ManifestVersion
                });
            }
            catch { manifestJson = "{}"; }
        }

        string embeddedResourcesJson = BuildEmbeddedResourcesJson(manifest, extensionRoot);

        // The shim is a self-executing function that sets up all the chrome.*
        // polyfills and the IPC bridge. Mirrors BrowserClient.mm lines 2316–3470.
        return $@"(function(){{
var extId='{extensionId}';
var manifest={manifestJson};
var runtimeRegistry=globalThis.__skewChromeById=globalThis.__skewChromeById||{{}};
var chrome=runtimeRegistry[extId]=runtimeRegistry[extId]||{{}};
var isExtensionPage=location.protocol==='skew-extension:';
if(isExtensionPage){{globalThis.chrome=chrome;globalThis.browser=chrome;}}
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
chrome.storage.sync=chrome.storage.sync||chrome.storage.local;
chrome.storage.session=chrome.storage.session||chrome.storage.local;

// --- chrome.tabs (minimal stubs) ---
chrome.tabs=chrome.tabs||{{}};
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

// --- chrome.action, browserAction, scripting and commands ---
chrome.action=chrome.action||{{}};
chrome.action.onClicked=chrome.action.onClicked||__skewEvent();
['setTitle','setIcon','setBadgeText','setBadgeBackgroundColor','enable','disable'].forEach(function(name){{
  chrome.action[name]=chrome.action[name]||function(details,cb){{
    var p=Promise.resolve(); if(typeof cb==='function')p.then(cb); return p;
  }};
}});
chrome.browserAction=chrome.browserAction||chrome.action;

chrome.scripting=chrome.scripting||{{}};
chrome.scripting.executeScript=chrome.scripting.executeScript||function(injection,cb){{
  var p=__skewExtCall('scripting.executeScript',{{injection:injection||{{}}}});
  if(typeof cb==='function')p.then(cb); return p;
}};

chrome.commands=chrome.commands||{{}};
chrome.commands.onCommand=chrome.commands.onCommand||__skewEvent();

// --- chrome.i18n ---
chrome.i18n=chrome.i18n||{{}};
chrome.i18n.getUILanguage=chrome.i18n.getUILanguage||function(){{return 'en';}};
chrome.i18n.getMessage=chrome.i18n.getMessage||function(name){{return name||'';}};

// --- chrome.extension ---
chrome.extension=chrome.extension||{{}};
chrome.extension.getURL=chrome.extension.getURL||runtime.getURL;

// --- globalThis.browser mirror ---
if(isExtensionPage){{globalThis.browser=chrome;}}

}})();";
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
