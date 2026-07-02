using System.Text.Json;

namespace Readmd.Diagrams;

/// <summary>
/// Builds a self-contained HTML page that renders a mermaid diagram client-side (as vector SVG via
/// the bundled mermaid.js) inside a pan/zoom viewer. Because zooming is done with a CSS transform on
/// the vector SVG — not the browser's own page zoom (which caps at ~500%) — a wide/long diagram can be
/// enlarged without limit and stays crisp at any scale.
/// </summary>
public static class MermaidHtml
{
    /// <summary>Returns a complete HTML document that renders <paramref name="source"/> with mermaid in a zoom/pan viewer.</summary>
    public static string BuildStandalonePage(string source, bool dark)
    {
        var config = MermaidTheme.ConfigJson(dark);           // already has startOnLoad:false
        var srcLiteral = JsonSerializer.Serialize(source);    // safe JS string literal
        var bg = dark ? "#0d1117" : "#ffffff";
        var fg = dark ? "#e6edf3" : "#1f2328";
        var panel = dark ? "#161b22" : "#f6f8fa";
        var border = dark ? "#30363d" : "#d0d7de";

        // Tokens (not C# interpolation) so the JS/CSS braces don't need escaping. Replace the theme
        // tokens and content first, then the mermaid.js blob LAST so its bytes aren't rescanned.
        const string template = """
<!doctype html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Mermaid diagram</title>
<style>
  html,body{margin:0;height:100%;overflow:hidden;background:__BG__;color:__FG__;font-family:Segoe UI,system-ui,sans-serif}
  #bar{position:fixed;top:10px;left:50%;transform:translateX(-50%);display:flex;gap:6px;z-index:10}
  #bar button{background:__PANEL__;color:__FG__;border:1px solid __BORDER__;border-radius:6px;min-width:36px;padding:6px 10px;cursor:pointer;font-size:14px}
  #bar button:hover{border-color:#4c8dff}
  #stage{position:fixed;inset:0;overflow:hidden;cursor:grab;touch-action:none}
  #stage.grabbing{cursor:grabbing}
  #canvas{transform-origin:0 0;will-change:transform}
  #d svg{display:block}
  #hint{position:fixed;bottom:10px;left:50%;transform:translateX(-50%);opacity:.6;font-size:12px;pointer-events:none}
</style></head>
<body>
  <div id="bar">
    <button data-a="out" title="Zoom out (-)">&#8722;</button>
    <button data-a="fit" title="Fit to window (0)">Fit</button>
    <button data-a="in" title="Zoom in (+)">+</button>
    <button data-a="reset" title="Actual size (1:1)">1:1</button>
  </div>
  <div id="stage"><div id="canvas"><div id="d">Rendering&#8230;</div></div></div>
  <div id="hint">scroll to zoom &#183; drag to pan</div>
  <script>__MERMAIDJS__</script>
  <script>
  (function(){
    var stage=document.getElementById('stage'),canvas=document.getElementById('canvas'),d=document.getElementById('d');
    var st={scale:1,x:0,y:0,base:1,natW:800,natH:600};
    function apply(){canvas.style.transform='translate('+st.x+'px,'+st.y+'px) scale('+st.scale+')';}
    function fit(){
      var sw=stage.clientWidth,sh=stage.clientHeight;
      var b=Math.min((sw*0.94)/st.natW,(sh*0.9)/st.natH);
      if(!isFinite(b)||b<=0)b=1;
      st.base=b;st.scale=b;
      st.x=(sw-st.natW*st.scale)/2;st.y=(sh-st.natH*st.scale)/2;apply();
    }
    function actual(){
      st.scale=1;
      st.x=(stage.clientWidth-st.natW)/2;st.y=(stage.clientHeight-st.natH)/2;apply();
    }
    function zoom(f,cx,cy){
      var prev=st.scale,next=Math.min(80,Math.max(0.02,prev*f));
      if(cx==null){cx=stage.clientWidth/2;cy=stage.clientHeight/2;}
      st.x=cx-(cx-st.x)*(next/prev);st.y=cy-(cy-st.y)*(next/prev);st.scale=next;apply();
    }
    stage.addEventListener('wheel',function(e){e.preventDefault();zoom(e.deltaY<0?1.15:1/1.15,e.clientX,e.clientY);},{passive:false});
    stage.addEventListener('dblclick',function(e){zoom(1.6,e.clientX,e.clientY);});
    var drag=false,sx=0,sy=0;
    stage.addEventListener('pointerdown',function(e){drag=true;sx=e.clientX-st.x;sy=e.clientY-st.y;stage.setPointerCapture(e.pointerId);stage.classList.add('grabbing');});
    stage.addEventListener('pointermove',function(e){if(!drag)return;st.x=e.clientX-sx;st.y=e.clientY-sy;apply();});
    stage.addEventListener('pointerup',function(e){drag=false;stage.classList.remove('grabbing');try{stage.releasePointerCapture(e.pointerId);}catch(_){}});
    document.getElementById('bar').addEventListener('click',function(e){
      var a=e.target.getAttribute('data-a');if(!a)return;
      if(a==='in')zoom(1.25);else if(a==='out')zoom(1/1.25);else if(a==='fit')fit();else if(a==='reset')actual();
    });
    window.addEventListener('keydown',function(e){
      if(e.key==='+'||e.key==='=')zoom(1.25);else if(e.key==='-'||e.key==='_')zoom(1/1.25);
      else if(e.key==='0')fit();else if(e.key==='1')actual();
    });
    window.addEventListener('resize',fit);
    function boot(){
      try{
        mermaid.initialize(__CONFIG__);
        mermaid.render('readmd-graph',__SRC__).then(function(r){
          d.innerHTML=r.svg;
          var s=d.querySelector('svg');
          if(s){
            var vb=s.viewBox&&s.viewBox.baseVal;
            st.natW=(vb&&vb.width)||parseFloat(s.getAttribute('width'))||s.getBoundingClientRect().width||800;
            st.natH=(vb&&vb.height)||parseFloat(s.getAttribute('height'))||s.getBoundingClientRect().height||600;
            s.removeAttribute('style');s.setAttribute('width',st.natW);s.setAttribute('height',st.natH);s.style.display='block';
          }
          fit();
        }).catch(function(err){d.textContent='Mermaid error: '+err;});
      }catch(err){d.textContent='Mermaid error: '+err;}
    }
    boot();
  })();
  </script>
</body></html>
""";

        return template
            .Replace("__BG__", bg)
            .Replace("__FG__", fg)
            .Replace("__PANEL__", panel)
            .Replace("__BORDER__", border)
            .Replace("__CONFIG__", config)
            .Replace("__SRC__", srcLiteral)
            .Replace("__MERMAIDJS__", BundledAssets.MermaidJs);
    }
}
